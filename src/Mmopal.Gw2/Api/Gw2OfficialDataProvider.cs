using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
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
    private const int IdsPerRequest = 200;
    private const string CurrentTransactionsPath = "v2/commerce/transactions/current";
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
        var (buys, sells) = await GetBuysAndSellsAsync<Gw2CurrentTransaction>(CurrentTransactionsPath, cancellationToken).ConfigureAwait(false);
        var itemIds = DistinctItemIds(buys, sells, row => row.ItemId);
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
            var granted = CachePermissions(permissions);
            _inventoryScopeUnavailable = !HasInventoryScopes(granted);

            string? accountName = null;
            if (granted.Contains("account"))
            {
                var account = await GetAsync<Gw2AccountInfo>("v2/account", cancellationToken).ConfigureAwait(false);
                accountName = account.Name;
            }

            long? coinCopper = null;
            if (granted.Contains("wallet"))
            {
                var balance = await GetBalanceAsync(cancellationToken).ConfigureAwait(false);
                coinCopper = balance?.Copper;
            }

            var message = DescribeKeyScopes(
                missing: MissingScopes(granted, "account", "tradingpost", "wallet"),
                missingInventoryScopes: MissingScopes(granted, "characters", "inventories"));
            return new Gw2ApiKeyValidationResult(true, accountName, permissions, coinCopper, message);
        }
        catch (Exception exception)
        {
            return new Gw2ApiKeyValidationResult(false, null, Array.Empty<string>(), null, exception.Message);
        }
    }

    private HashSet<string> CachePermissions(IEnumerable<string>? permissions)
    {
        _cachedPermissions = new HashSet<string>(permissions ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        return _cachedPermissions;
    }

    private static bool HasInventoryScopes(HashSet<string> granted) =>
        granted.Contains("characters") && granted.Contains("inventories");

    private static string[] MissingScopes(HashSet<string> granted, params string[] required) =>
        required.Where(scope => !granted.Contains(scope)).ToArray();

    private static string DescribeKeyScopes(string[] missing, string[] missingInventoryScopes)
    {
        if (missing.Length > 0)
        {
            return $"API key valid, but missing recommended scope(s): {string.Join(", ", missing)}.";
        }

        if (missingInventoryScopes.Length > 0)
        {
            return $"API key verified! (Optional: add '{string.Join(", ", missingInventoryScopes)}' to enable bag-aware smart auto-sell.)";
        }

        return "API key verified with account, tradingpost, wallet, characters, and inventories scopes.";
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
        foreach (var item in await ReadItemsAsync(missing, cancellationToken).ConfigureAwait(false))
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
        foreach (var item in await ReadItemsAsync(itemIds.Distinct(), cancellationToken).ConfigureAwait(false))
        {
            if (!string.IsNullOrWhiteSpace(item.Name)) names[item.Id] = item.Name;
        }

        return names;
    }

    private async Task<IReadOnlyDictionary<int, Gw2ListingPrice>> ReadListingPricesAsync(
        IReadOnlyList<int> itemIds,
        CancellationToken cancellationToken)
    {
        var prices = await GetByIdsAsync<Gw2ListingPrice>("v2/commerce/prices", itemIds.Distinct(), cancellationToken).ConfigureAwait(false);
        var listings = new Dictionary<int, Gw2ListingPrice>();
        foreach (var price in prices)
        {
            listings[price.Id] = price;
        }

        return listings;
    }

    private Task<List<Gw2Item>> ReadItemsAsync(IEnumerable<int> itemIds, CancellationToken cancellationToken) =>
        GetByIdsAsync<Gw2Item>("v2/items", itemIds, cancellationToken);

    /// <summary>Fetches <paramref name="path"/>?ids=… in chunks of <see cref="IdsPerRequest"/> ids.</summary>
    private async Task<List<T>> GetByIdsAsync<T>(string path, IEnumerable<int> ids, CancellationToken cancellationToken)
    {
        var results = new List<T>();
        foreach (var chunk in ids.Chunk(IdsPerRequest))
        {
            var idList = string.Join(',', chunk);
            results.AddRange(await GetAsync<List<T>>($"{path}?ids={idList}", cancellationToken).ConfigureAwait(false));
        }

        return results;
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

    private static int[] DistinctItemIds<T>(IEnumerable<T> buys, IEnumerable<T> sells, Func<T, int> itemId) =>
        buys.Select(itemId).Concat(sells.Select(itemId)).Distinct().ToArray();

    /// <summary>Reads every page of <paramref name="basePath"/>/buys, then of <paramref name="basePath"/>/sells.</summary>
    private async Task<(List<T> Buys, List<T> Sells)> GetBuysAndSellsAsync<T>(string basePath, CancellationToken cancellationToken)
    {
        var buys = await GetAllPagesAsync<T>($"{basePath}/buys", cancellationToken).ConfigureAwait(false);
        var sells = await GetAllPagesAsync<T>($"{basePath}/sells", cancellationToken).ConfigureAwait(false);
        return (buys, sells);
    }

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
        using var request = CreateRequest(url);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        EnsureAccepted(response);
        var pageTotal = ReadPageTotal(response);
        var items = await ReadJsonAsync<List<T>>(response, cancellationToken).ConfigureAwait(false);
        return (items, pageTotal);
    }

    private async Task<T> GetAsync<T>(string relativeUrl, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(relativeUrl);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        EnsureAccepted(response);
        return await ReadJsonAsync<T>(response, cancellationToken).ConfigureAwait(false);
    }

    private HttpRequestMessage CreateRequest(string relativeUrl)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, relativeUrl);
        if (!string.IsNullOrWhiteSpace(_apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        }

        return request;
    }

    private static void EnsureAccepted(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new InvalidOperationException("Guild Wars 2 rejected the API key.");
        }

        response.EnsureSuccessStatusCode();
    }

    private static int ReadPageTotal(HttpResponseMessage response) =>
        response.Headers.TryGetValues("X-Page-Total", out var values) &&
        int.TryParse(values.FirstOrDefault(), out var pageTotal) &&
        pageTotal > 0
            ? pageTotal
            : 1;

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Guild Wars 2 returned an empty response.");
    }
}
