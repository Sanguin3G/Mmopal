using System.Text.Json;
using System.Text.Json.Serialization;
using Mmopal.Contracts;
using Mmopal.Infrastructure;

namespace Mmopal.Gw2;

/// <summary>Where the scanner gets its data: GW2 item metadata (SQLite-cached), order books, and DataWars2 7-day volume and prices.</summary>
public sealed partial class Gw2MarketScanner
{
    private async Task<Dictionary<int, Gw2CachedItemInfo>> FetchItemMetadataAsync(
        IEnumerable<int> itemIds,
        CancellationToken cancellationToken)
    {
        var distinctIds = itemIds.Distinct().ToArray();
        Dictionary<int, Gw2CachedItemInfo> map;

        if (_itemCache is not null)
        {
            await EnsureCacheInitializedAsync(cancellationToken).ConfigureAwait(false);
            map = await _itemCache.GetCachedItemsAsync(distinctIds, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            map = new Dictionary<int, Gw2CachedItemInfo>();
        }

        var missing = distinctIds.Where(id => !map.ContainsKey(id)).ToArray();
        if (missing.Length == 0)
        {
            return map;
        }

        var newlyFetched = new List<Gw2CachedItemInfo>();
        var now = DateTimeOffset.UtcNow;
        foreach (var chunk in missing.Chunk(ApiBatchSize))
        {
            var ids = string.Join(',', chunk);
            var items = await GetJsonAsync<List<Gw2ItemMetadata>>($"v2/items?ids={ids}", cancellationToken).ConfigureAwait(false);
            foreach (var item in items)
            {
                if (string.IsNullOrWhiteSpace(item.Name)) continue;
                var info = new Gw2CachedItemInfo(
                    ItemId: item.Id,
                    Name: item.Name.Trim(),
                    Rarity: string.IsNullOrWhiteSpace(item.Rarity) ? "Basic" : item.Rarity.Trim(),
                    ItemType: item.Type?.Trim() ?? string.Empty,
                    VendorValueCopper: item.VendorValue,
                    UpdatedAt: now);
                map[item.Id] = info;
                newlyFetched.Add(info);
            }
        }

        if (_itemCache is not null && newlyFetched.Count > 0)
        {
            await _itemCache.UpsertItemsAsync(newlyFetched, cancellationToken).ConfigureAwait(false);
        }

        return map;
    }

    private async Task EnsureCacheInitializedAsync(CancellationToken cancellationToken)
    {
        if (_cacheInitialized || _itemCache is null)
        {
            return;
        }

        await _cacheInitGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_cacheInitialized)
            {
                await _itemCache.InitializeAsync(cancellationToken).ConfigureAwait(false);
                _cacheInitialized = true;
            }
        }
        finally
        {
            _cacheInitGate.Release();
        }
    }

    private async Task<Dictionary<int, Gw2ListingsBook>> FetchOrderBooksBestEffortAsync(
        IEnumerable<int> itemIds,
        CancellationToken cancellationToken)
    {
        var map = new Dictionary<int, Gw2ListingsBook>();
        foreach (var chunk in itemIds.Distinct().Chunk(ApiBatchSize))
        {
            try
            {
                var ids = string.Join(',', chunk);
                var books = await GetJsonAsync<List<Gw2ListingsBook>>($"v2/commerce/listings?ids={ids}", cancellationToken).ConfigureAwait(false);
                foreach (var book in books)
                {
                    map[book.Id] = book;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Keep scan resilient if listings endpoint hiccups on a batch.
            }
        }

        return map;
    }

    private async Task<Dictionary<int, MarketItemStats>?> FetchDataWarsStatsBestEffortAsync(CancellationToken cancellationToken)
    {
        try
        {
            // DataWars2 streams ~1.8 MB slowly: uncompressed it took 120 s+ and hit the old 30 s timeout
            // (the scan then silently fell back to sampling). Compressed it is ~0.4 MB in ~20 s.
            using var ownedClient = _velocityHttpClient is null
                ? new HttpClient(new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All })
                {
                    Timeout = TimeSpan.FromSeconds(90)
                }
                : null;
            var client = _velocityHttpClient ?? ownedClient!;
            using var response = await client.GetAsync(
                "https://api.datawars2.ie/gw2/v1/items/json?fields=id,name,1d_buy_sold,1d_sell_sold,7d_buy_sold,7d_sell_sold,7d_buy_price_avg,7d_sell_price_avg",
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            return ParseDataWarsVelocity(doc.RootElement);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Trades per day per item: min(units sold into buy orders, units bought from listings), as a 7-day daily
    /// average (falls back to the last day). "buy_sold" is how fast our buy orders can fill; "sell_sold" how fast our
    /// listings can sell. A flip needs both, so the smaller one is the item's real throughput.
    /// </summary>
    internal static Dictionary<int, MarketItemStats>? ParseDataWarsVelocity(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        static double Read(JsonElement element, string field) =>
            element.TryGetProperty(field, out var prop) && prop.ValueKind == JsonValueKind.Number && prop.TryGetDouble(out var value)
                ? Math.Max(0, value)
                : -1;

        var map = new Dictionary<int, MarketItemStats>();
        foreach (var elem in root.EnumerateArray())
        {
            if (!elem.TryGetProperty("id", out var idProp) || !idProp.TryGetInt32(out var id))
            {
                continue;
            }

            var weekBuy = Read(elem, "7d_buy_sold");
            var weekSell = Read(elem, "7d_sell_sold");
            double perDay;
            if (weekBuy >= 0 && weekSell >= 0)
            {
                perDay = Math.Min(weekBuy, weekSell) / 7.0;
            }
            else
            {
                var dayBuy = Read(elem, "1d_buy_sold");
                var daySell = Read(elem, "1d_sell_sold");
                if (dayBuy < 0 || daySell < 0) continue;
                perDay = Math.Min(dayBuy, daySell);
            }

            var buyAvg = Read(elem, "7d_buy_price_avg");
            var sellAvg = Read(elem, "7d_sell_price_avg");
            map[id] = new MarketItemStats(
                perDay,
                buyAvg > 0 ? (long)Math.Round(buyAvg) : null,
                sellAvg > 0 ? (long)Math.Round(sellAvg) : null,
                elem.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String ? nameProp.GetString() : null);
        }

        return map;
    }

    /// <summary>
    /// Names that more than one tradable item carries (3,331 of ~28,000 on 22 Nov: "Berserker's Ring" is 11 items of
    /// different rarity and level). A Trading Post search by name lists them all and the dialog title cannot tell
    /// them apart, so such an item is bought or priced as the wrong variant: Berserker's Ring showed a live bid of
    /// 41s 78c against 30s 04c on the API every session, Berserker's Amulet a total of 27s 87c whatever was typed.
    /// </summary>
    public static HashSet<string> SharedNames(IEnumerable<MarketItemStats> stats) =>
        stats
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Name))
            .GroupBy(entry => ItemNameNormalizer.Normalize(entry.Name!), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private async Task<T> GetJsonAsync<T>(string relativeUrl, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(relativeUrl, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
               ?? throw new InvalidDataException($"GW2 API returned empty payload for '{relativeUrl}'.");
    }

    private sealed record Gw2CommercePrice(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("buys")] Gw2PriceSide? Buys,
        [property: JsonPropertyName("sells")] Gw2PriceSide? Sells);

    private sealed record Gw2PriceSide(
        [property: JsonPropertyName("quantity")] long Quantity,
        [property: JsonPropertyName("unit_price")] long UnitPrice);

    private sealed record Gw2ItemMetadata(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("rarity")] string? Rarity,
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("vendor_value")] long VendorValue);

    private sealed record Gw2ListingsBook(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("buys")] List<Gw2ListingLevel>? Buys,
        [property: JsonPropertyName("sells")] List<Gw2ListingLevel>? Sells);

    private sealed record Gw2ListingLevel(
        [property: JsonPropertyName("listings")] int Listings,
        [property: JsonPropertyName("unit_price")] long UnitPrice,
        [property: JsonPropertyName("quantity")] long Quantity);
}
