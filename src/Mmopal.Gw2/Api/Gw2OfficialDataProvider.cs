using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mmopal.Contracts;
using Mmopal.Infrastructure;

namespace Mmopal.Gw2;

/// <summary>
/// Reads current transactions, history, delivery box, listing prices, and coin balance from the official Guild Wars 2 API.
/// The lowest ask and the highest bid come from commerce/prices. The key is sent as a bearer token and is not written into the request URL.
/// </summary>
public sealed partial class Gw2OfficialDataProvider : IGw2DataProvider
{
    private const int CoinCurrencyId = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private HashSet<string>? _cachedPermissions;
    private bool _inventoryScopeUnavailable;

    public Gw2OfficialDataProvider(HttpClient httpClient, string apiKey)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
    }

    private const int TransactionPageSize = 200;

    public async Task<IReadOnlyList<Transaction>> GetTransactionsAsync(CancellationToken cancellationToken = default)
    {
        RequireKey();
        var buys = await GetAllPagesAsync<Gw2CurrentTransaction>("v2/commerce/transactions/current/buys", cancellationToken).ConfigureAwait(false);
        var sells = await GetAllPagesAsync<Gw2CurrentTransaction>("v2/commerce/transactions/current/sells", cancellationToken).ConfigureAwait(false);
        var itemIds = buys.Select(row => row.ItemId).Concat(sells.Select(row => row.ItemId)).Distinct().ToArray();
        var names = await ReadItemNamesAsync(itemIds, cancellationToken).ConfigureAwait(false);
        var listings = await ReadListingPricesAsync(itemIds, cancellationToken).ConfigureAwait(false);
        return buys.Select(row => ToTransaction(TransactionType.Buy, row, names, listings))
            .Concat(sells.Select(row => ToTransaction(TransactionType.Sell, row, names, listings)))
            .ToArray();
    }

    public async Task<AccountBalance?> GetBalanceAsync(CancellationToken cancellationToken = default)
    {
        RequireKey();
        var wallet = await GetAsync<List<Gw2WalletEntry>>("v2/account/wallet", cancellationToken).ConfigureAwait(false);
        var coin = wallet.FirstOrDefault(entry => entry.Id == CoinCurrencyId);
        return new AccountBalance(coin?.Value ?? 0);
    }

    public async Task<Gw2ApiKeyValidationResult> ValidateApiKeyAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            return new Gw2ApiKeyValidationResult(false, null, Array.Empty<string>(), null, "No API key configured.");
        }

        try
        {
            var tokenInfo = await GetAsync<Gw2TokenInfo>("v2/tokeninfo", cancellationToken).ConfigureAwait(false);
            var permissions = tokenInfo.Permissions ?? Array.Empty<string>();
            _cachedPermissions = new HashSet<string>(permissions, StringComparer.OrdinalIgnoreCase);
            _inventoryScopeUnavailable =
                !_cachedPermissions.Contains("characters") ||
                !_cachedPermissions.Contains("inventories");
            string? accountName = null;
            if (permissions.Contains("account", StringComparer.OrdinalIgnoreCase))
            {
                var account = await GetAsync<Gw2AccountInfo>("v2/account", cancellationToken).ConfigureAwait(false);
                accountName = account.Name;
            }

            long? coinCopper = null;
            if (permissions.Contains("wallet", StringComparer.OrdinalIgnoreCase))
            {
                var balance = await GetBalanceAsync(cancellationToken).ConfigureAwait(false);
                coinCopper = balance?.Copper;
            }

            var missing = new[] { "account", "tradingpost", "wallet" }
                .Where(required => !permissions.Contains(required, StringComparer.OrdinalIgnoreCase))
                .ToArray();
            var missingInventoryScopes = new[] { "characters", "inventories" }
                .Where(required => !permissions.Contains(required, StringComparer.OrdinalIgnoreCase))
                .ToArray();
            var message = missing.Length == 0
                ? missingInventoryScopes.Length == 0
                    ? "API key verified with account, tradingpost, wallet, characters, and inventories scopes."
                    : $"API key verified! (Optional: add '{string.Join(", ", missingInventoryScopes)}' to enable bag-aware smart auto-sell.)"
                : $"API key valid, but missing recommended scope(s): {string.Join(", ", missing)}.";
            return new Gw2ApiKeyValidationResult(true, accountName, permissions, coinCopper, message);
        }
        catch (Exception exception)
        {
            return new Gw2ApiKeyValidationResult(false, null, Array.Empty<string>(), null, exception.Message);
        }
    }

    private void RequireKey()
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            throw new InvalidOperationException("Set your GW2 API key in Settings or MMOPAL_Native__Gw2ApiKey to read Guild Wars 2 account data.");
        }
    }

    public async Task<IReadOnlyDictionary<int, Gw2LivePriceQuote>> GetLiveMarketQuotesAsync(
        IReadOnlyList<int> itemIds,
        CancellationToken cancellationToken = default)
    {
        if (itemIds is null || itemIds.Count == 0)
        {
            return new Dictionary<int, Gw2LivePriceQuote>();
        }

        var raw = await ReadListingPricesAsync(itemIds, cancellationToken).ConfigureAwait(false);
        var quotes = new Dictionary<int, Gw2LivePriceQuote>(raw.Count);
        foreach (var (id, price) in raw)
        {
            quotes[id] = new Gw2LivePriceQuote(
                ItemId: id,
                HighestBuyCopper: price.Buys?.UnitPrice ?? 0,
                BuyQuantity: price.Buys?.Quantity ?? 0,
                LowestSellCopper: price.Sells?.UnitPrice ?? 0,
                SellQuantity: price.Sells?.Quantity ?? 0);
        }

        return quotes;
    }

    public async Task<IReadOnlyDictionary<int, Gw2CachedItemInfo>> ResolveItemMetadataAsync(
        IEnumerable<int> itemIds,
        TradingLedgerStore? itemCache,
        CancellationToken cancellationToken)
    {
        var distinct = itemIds.Distinct().ToArray();
        var map = itemCache is not null
            ? await itemCache.GetCachedItemsAsync(distinct, cancellationToken).ConfigureAwait(false)
            : new Dictionary<int, Gw2CachedItemInfo>();

        var missing = distinct.Where(id => !map.ContainsKey(id)).ToArray();
        if (missing.Length == 0)
        {
            return map;
        }

        var newlyFetched = new List<Gw2CachedItemInfo>();
        var now = DateTimeOffset.UtcNow;
        foreach (var chunk in missing.Chunk(200))
        {
            var ids = string.Join(',', chunk);
            var items = await GetAsync<List<Gw2Item>>($"v2/items?ids={ids}", cancellationToken).ConfigureAwait(false);
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

        if (itemCache is not null && newlyFetched.Count > 0)
        {
            await itemCache.UpsertItemsAsync(newlyFetched, cancellationToken).ConfigureAwait(false);
        }

        return map;
    }

    private async Task<IReadOnlyDictionary<int, string>> ReadItemNamesAsync(
        IEnumerable<int> itemIds,
        CancellationToken cancellationToken)
    {
        var names = new Dictionary<int, string>();
        foreach (var chunk in itemIds.Distinct().Chunk(200))
        {
            var ids = string.Join(',', chunk);
            var items = await GetAsync<List<Gw2Item>>($"v2/items?ids={ids}", cancellationToken).ConfigureAwait(false);
            foreach (var item in items)
            {
                if (!string.IsNullOrWhiteSpace(item.Name)) names[item.Id] = item.Name;
            }
        }

        return names;
    }

    private async Task<IReadOnlyDictionary<int, Gw2ListingPrice>> ReadListingPricesAsync(
        IReadOnlyList<int> itemIds,
        CancellationToken cancellationToken)
    {
        var listings = new Dictionary<int, Gw2ListingPrice>();
        foreach (var chunk in itemIds.Distinct().Chunk(200))
        {
            var ids = string.Join(',', chunk);
            var prices = await GetAsync<List<Gw2ListingPrice>>($"v2/commerce/prices?ids={ids}", cancellationToken).ConfigureAwait(false);
            foreach (var price in prices)
            {
                listings[price.Id] = price;
            }
        }

        return listings;
    }

    private static Transaction ToTransaction(
        TransactionType type,
        Gw2CurrentTransaction row,
        IReadOnlyDictionary<int, string> names,
        IReadOnlyDictionary<int, Gw2ListingPrice> listings)
    {
        var name = names.TryGetValue(row.ItemId, out var resolved) ? resolved : row.ItemId.ToString();
        listings.TryGetValue(row.ItemId, out var listing);
        // The old CSV called the lowest ask MinSellPrice and the highest bid MinBuyPrice.
        return new Transaction(
            type,
            name,
            row.Price,
            row.Quantity,
            row.Created,
            PositiveUnitPrice(listing?.Sells),
            PositiveUnitPrice(listing?.Buys));
    }

    private static long? PositiveUnitPrice(Gw2PriceLevel? level) =>
        level is { UnitPrice: > 0 } ? level.UnitPrice : null;

    private async Task<List<T>> GetAllPagesAsync<T>(string basePath, CancellationToken cancellationToken)
    {
        var (firstPage, pageTotal) = await GetPageAsync<T>(basePath, 0, cancellationToken).ConfigureAwait(false);
        var results = new List<T>(firstPage);
        for (var page = 1; page < pageTotal; page++)
        {
            var (pageItems, _) = await GetPageAsync<T>(basePath, page, cancellationToken).ConfigureAwait(false);
            results.AddRange(pageItems);
        }

        return results;
    }

    private async Task<(List<T> Items, int PageTotal)> GetPageAsync<T>(string basePath, int page, CancellationToken cancellationToken)
    {
        var separator = basePath.Contains('?') ? '&' : '?';
        var url = $"{basePath}{separator}page={page}&page_size={TransactionPageSize}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            throw new InvalidOperationException("Guild Wars 2 rejected the API key.");
        }

        response.EnsureSuccessStatusCode();
        var pageTotal = 1;
        if (response.Headers.TryGetValues("X-Page-Total", out var values) &&
            int.TryParse(values.FirstOrDefault(), out var parsedTotal) &&
            parsedTotal > 0)
        {
            pageTotal = parsedTotal;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var items = await JsonSerializer.DeserializeAsync<List<T>>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Guild Wars 2 returned an empty response.");
        return (items, pageTotal);
    }

    private async Task<T> GetAsync<T>(string relativeUrl, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, relativeUrl);
        if (!string.IsNullOrWhiteSpace(_apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        }

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            throw new InvalidOperationException("Guild Wars 2 rejected the API key.");
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Guild Wars 2 returned an empty response.");
    }

}
