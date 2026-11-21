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
public sealed class Gw2OfficialDataProvider : IGw2DataProvider
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

    public async Task<IReadOnlyList<Gw2LedgerTransaction>> GetHistoryTransactionsAsync(
        TradingLedgerStore? itemCache = null,
        CancellationToken cancellationToken = default)
    {
        RequireKey();
        var buys = await GetAllPagesAsync<Gw2HistoryTransaction>("v2/commerce/transactions/history/buys", cancellationToken).ConfigureAwait(false);
        var sells = await GetAllPagesAsync<Gw2HistoryTransaction>("v2/commerce/transactions/history/sells", cancellationToken).ConfigureAwait(false);
        var itemIds = buys.Select(row => row.ItemId).Concat(sells.Select(row => row.ItemId)).Distinct().ToArray();
        var itemMap = await ResolveItemMetadataAsync(itemIds, itemCache, cancellationToken).ConfigureAwait(false);

        return buys.Select(row => ToLedgerTransaction(TransactionType.Buy, row, itemMap))
            .Concat(sells.Select(row => ToLedgerTransaction(TransactionType.Sell, row, itemMap)))
            .OrderBy(row => row.PurchasedAt)
            .ThenBy(row => row.Id)
            .ToArray();
    }

    public async Task<Gw2DeliverySnapshot> GetDeliveryAsync(CancellationToken cancellationToken = default)
    {
        RequireKey();
        var response = await GetAsync<Gw2DeliveryResponse>("v2/commerce/delivery", cancellationToken).ConfigureAwait(false);
        var items = (response.Items ?? [])
            .Select(entry => new Gw2DeliveryItem(entry.Id, entry.Count))
            .ToArray();
        return new Gw2DeliverySnapshot(response.Coins, items, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Reads all unbound (tradable on Trading Post) item IDs across character bags and shared inventory
    /// when the API key grants 'characters' and 'inventories'. Fails closed without spamming errors when missing.
    /// </summary>
    public async Task<(bool Available, IReadOnlySet<int> ItemIds)> GetUnboundInventoryItemIdsAsync(
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_apiKey) || _inventoryScopeUnavailable)
        {
            return (false, new HashSet<int>());
        }

        try
        {
            if (_cachedPermissions is null)
            {
                var tokenInfo = await GetAsync<Gw2TokenInfo>("v2/tokeninfo", cancellationToken).ConfigureAwait(false);
                _cachedPermissions = new HashSet<string>(
                    tokenInfo.Permissions ?? Array.Empty<string>(),
                    StringComparer.OrdinalIgnoreCase);
            }

            if (!_cachedPermissions.Contains("characters") || !_cachedPermissions.Contains("inventories"))
            {
                _inventoryScopeUnavailable = true;
                return (false, new HashSet<int>());
            }

            var itemIds = new HashSet<int>();
            var characters = await GetAsync<List<Gw2CharacterEntry?>>("v2/characters?ids=all", cancellationToken).ConfigureAwait(false);
            foreach (var character in characters)
            {
                if (character?.Bags is null)
                {
                    continue;
                }

                foreach (var bag in character.Bags)
                {
                    if (bag?.Inventory is null)
                    {
                        continue;
                    }

                    foreach (var slot in bag.Inventory)
                    {
                        if (slot is { Id: > 0, Count: > 0 } && string.IsNullOrWhiteSpace(slot.Binding))
                        {
                            itemIds.Add(slot.Id);
                        }
                    }
                }
            }

            // Shared inventory slots (best-effort; do not fail if omitted by mock/proxy).
            try
            {
                var shared = await GetAsync<List<Gw2InventorySlot?>>("v2/account/inventory", cancellationToken).ConfigureAwait(false);
                foreach (var slot in shared)
                {
                    if (slot is { Id: > 0, Count: > 0 } && string.IsNullOrWhiteSpace(slot.Binding))
                    {
                        itemIds.Add(slot.Id);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Character bags already succeeded; ignore optional shared-slot fetch failures.
            }

            return (true, itemIds);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized)
        {
            _inventoryScopeUnavailable = true;
            return (false, new HashSet<int>());
        }
        catch (InvalidOperationException)
        {
            _inventoryScopeUnavailable = true;
            return (false, new HashSet<int>());
        }
    }

    /// <summary>
    /// Reads the latest page of filled buy history transactions. On initial call (afterTransactionId == null),
    /// establishes the highest watermark without emitting historical pre-session fills.
    /// </summary>
    public async Task<(long HighestId, IReadOnlyList<(long Id, int ItemId, long Quantity)> NewFills)> GetRecentHistoryBuyItemIdsAsync(
        long? afterTransactionId = null,
        CancellationToken cancellationToken = default)
    {
        RequireKey();
        var (firstPage, _) = await GetPageAsync<Gw2HistoryTransaction>(
            "v2/commerce/transactions/history/buys",
            0,
            cancellationToken).ConfigureAwait(false);

        if (firstPage.Count == 0)
        {
            return (afterTransactionId ?? 0, Array.Empty<(long, int, long)>());
        }

        var highestId = firstPage.Max(row => row.Id);
        if (afterTransactionId is null)
        {
            return (Math.Max(0, highestId), Array.Empty<(long, int, long)>());
        }

        var baseline = afterTransactionId.Value;
        var fills = firstPage
            .Where(row => row.Id > baseline)
            .Select(row => (row.Id, row.ItemId, row.Quantity))
            .ToArray();
        return (Math.Max(baseline, highestId), fills);
    }

    public async Task<IReadOnlyList<Gw2ActiveOrder>> GetActiveOrdersDetailedAsync(
        TradingLedgerStore? itemCache = null,
        CancellationToken cancellationToken = default)
    {
        RequireKey();
        var buys = await GetAllPagesAsync<Gw2CurrentTransaction>("v2/commerce/transactions/current/buys", cancellationToken).ConfigureAwait(false);
        var sells = await GetAllPagesAsync<Gw2CurrentTransaction>("v2/commerce/transactions/current/sells", cancellationToken).ConfigureAwait(false);
        var itemIds = buys.Select(row => row.ItemId).Concat(sells.Select(row => row.ItemId)).Distinct().ToArray();
        var itemMap = await ResolveItemMetadataAsync(itemIds, itemCache, cancellationToken).ConfigureAwait(false);
        var listings = await ReadListingPricesAsync(itemIds, cancellationToken).ConfigureAwait(false);

        return buys.Select(row => ToActiveOrder(TransactionType.Buy, row, itemMap, listings))
            .Concat(sells.Select(row => ToActiveOrder(TransactionType.Sell, row, itemMap, listings)))
            .OrderByDescending(order => order.CreatedAt)
            .ToArray();
    }

    public Task<Gw2PortfolioSyncResult> SyncPortfolioAsync(
        TradingLedgerStore store,
        CancellationToken cancellationToken = default) =>
        SyncPortfolioAsync(store, accountName: null, cancellationToken);

    public async Task<Gw2PortfolioSyncResult> SyncPortfolioAsync(
        TradingLedgerStore store,
        string? accountName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        RequireKey();
        await store.InitializeAsync(cancellationToken).ConfigureAwait(false);

        var resolvedAccount = accountName?.Trim();
        if (string.IsNullOrWhiteSpace(resolvedAccount))
        {
            try
            {
                var info = await GetAsync<Gw2AccountInfo>("v2/account", cancellationToken).ConfigureAwait(false);
                resolvedAccount = info.Name?.Trim();
            }
            catch when (!cancellationToken.IsCancellationRequested)
            {
                resolvedAccount = string.Empty;
            }
        }

        resolvedAccount ??= string.Empty;
        if (!string.IsNullOrEmpty(resolvedAccount))
        {
            await store.AdoptUnassignedRowsAsync(resolvedAccount, cancellationToken).ConfigureAwait(false);
        }

        var history = await GetHistoryTransactionsAsync(store, cancellationToken).ConfigureAwait(false);
        var newlyArchived = await store.UpsertTransactionsAsync(history, resolvedAccount, cancellationToken).ConfigureAwait(false);
        var allStored = await store.GetTransactionsAsync(string.IsNullOrEmpty(resolvedAccount) ? null : resolvedAccount, cancellationToken).ConfigureAwait(false);

        var balance = await GetBalanceAsync(cancellationToken).ConfigureAwait(false);
        var delivery = await GetDeliveryAsync(cancellationToken).ConfigureAwait(false);
        var activeOrders = await GetActiveOrdersDetailedAsync(store, cancellationToken).ConfigureAwait(false);

        long deliveryItemsCopper = 0;
        if (delivery.Items.Count > 0)
        {
            var deliveryIds = delivery.Items.Select(item => item.ItemId).Distinct().ToArray();
            var deliveryPrices = await ReadListingPricesAsync(deliveryIds, cancellationToken).ConfigureAwait(false);
            foreach (var entry in delivery.Items)
            {
                if (!deliveryPrices.TryGetValue(entry.ItemId, out var quote)) continue;
                var unitValue = PositiveUnitPrice(quote.Buys) ?? PositiveUnitPrice(quote.Sells) ?? 0;
                deliveryItemsCopper += unitValue * entry.Count;
            }
        }

        long activeBuysCopper = 0;
        long activeSellsNetCopper = 0;
        var openBuys = 0;
        var openSells = 0;
        foreach (var order in activeOrders)
        {
            if (order.Kind == TransactionType.Buy)
            {
                openBuys++;
                activeBuysCopper += order.UnitPriceCopper * order.Quantity;
            }
            else
            {
                openSells++;
                // 5% listing fee was already paid upfront from wallet when listing, so only 10% exchange fee remains.
                var netUnit = Math.Max(0, order.UnitPriceCopper - TradingIncomeAnalytics.CalculateExchangeFee(order.UnitPriceCopper));
                activeSellsNetCopper += netUnit * order.Quantity;
            }
        }

        var walletCopper = balance?.Copper ?? 0;
        var totalEquityCopper = walletCopper + delivery.CoinsCopper + deliveryItemsCopper + activeBuysCopper + activeSellsNetCopper;
        var snapshot = new Gw2EquitySnapshot(
            CapturedAt: DateTimeOffset.UtcNow,
            WalletCopper: walletCopper,
            DeliveryCopper: delivery.CoinsCopper,
            DeliveryItemsCopper: deliveryItemsCopper,
            ActiveBuysCopper: activeBuysCopper,
            ActiveSellsNetCopper: activeSellsNetCopper,
            TotalEquityCopper: totalEquityCopper,
            OpenBuyCount: openBuys,
            OpenSellCount: openSells);

        await store.SaveEquitySnapshotAsync(snapshot, resolvedAccount, cancellationToken).ConfigureAwait(false);

        return new Gw2PortfolioSyncResult(
            NewlyArchivedTransactions: newlyArchived,
            TotalArchivedTransactions: allStored.Count,
            ActiveOrders: activeOrders,
            Delivery: delivery,
            LatestSnapshot: snapshot,
            AccountName: resolvedAccount);
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

    private static Gw2LedgerTransaction ToLedgerTransaction(
        TransactionType kind,
        Gw2HistoryTransaction row,
        IReadOnlyDictionary<int, Gw2CachedItemInfo> itemMap)
    {
        var name = itemMap.TryGetValue(row.ItemId, out var info) ? info.Name : $"Item #{row.ItemId}";
        return new Gw2LedgerTransaction(
            Id: row.Id,
            Kind: kind,
            ItemId: row.ItemId,
            ItemName: name,
            UnitPriceCopper: row.Price,
            Quantity: row.Quantity,
            CreatedAt: row.Created,
            PurchasedAt: row.Purchased == default ? row.Created : row.Purchased);
    }

    private static Gw2ActiveOrder ToActiveOrder(
        TransactionType kind,
        Gw2CurrentTransaction row,
        IReadOnlyDictionary<int, Gw2CachedItemInfo> itemMap,
        IReadOnlyDictionary<int, Gw2ListingPrice> listings)
    {
        var name = itemMap.TryGetValue(row.ItemId, out var info) ? info.Name : $"Item #{row.ItemId}";
        listings.TryGetValue(row.ItemId, out var listing);
        var highestBuy = PositiveUnitPrice(listing?.Buys);
        var lowestSell = PositiveUnitPrice(listing?.Sells);

        var status = kind == TransactionType.Buy
            ? highestBuy is null ? "Unknown" : row.Price >= highestBuy.Value ? "Top of Book" : "Outbid"
            : lowestSell is null ? "Unknown" : row.Price <= lowestSell.Value ? "Top of Book" : "Undercut";

        return new Gw2ActiveOrder(
            Id: row.Id,
            Kind: kind,
            ItemId: row.ItemId,
            ItemName: name,
            UnitPriceCopper: row.Price,
            Quantity: row.Quantity,
            CreatedAt: row.Created,
            MarketHighestBuyCopper: highestBuy,
            MarketLowestSellCopper: lowestSell,
            BookStatus: status);
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

    private sealed record Gw2CurrentTransaction(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("item_id")] int ItemId,
        [property: JsonPropertyName("price")] long Price,
        [property: JsonPropertyName("quantity")] long Quantity,
        [property: JsonPropertyName("created")] DateTimeOffset Created);

    private sealed record Gw2HistoryTransaction(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("item_id")] int ItemId,
        [property: JsonPropertyName("price")] long Price,
        [property: JsonPropertyName("quantity")] long Quantity,
        [property: JsonPropertyName("created")] DateTimeOffset Created,
        [property: JsonPropertyName("purchased")] DateTimeOffset Purchased);

    private sealed record Gw2DeliveryResponse(
        [property: JsonPropertyName("coins")] long Coins,
        [property: JsonPropertyName("items")] Gw2DeliveryEntry[]? Items);

    private sealed record Gw2DeliveryEntry(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("count")] long Count);

    private sealed record Gw2Item(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("rarity")] string? Rarity,
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("vendor_value")] long VendorValue);

    private sealed record Gw2WalletEntry(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("value")] long Value);

    private sealed record Gw2ListingPrice(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("buys")] Gw2PriceLevel? Buys,
        [property: JsonPropertyName("sells")] Gw2PriceLevel? Sells);

    private sealed record Gw2PriceLevel(
        [property: JsonPropertyName("quantity")] long Quantity,
        [property: JsonPropertyName("unit_price")] long UnitPrice);

    private sealed record Gw2TokenInfo(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("permissions")] string[]? Permissions);

    private sealed record Gw2AccountInfo(
        [property: JsonPropertyName("name")] string? Name);

    private sealed record Gw2CharacterEntry(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("bags")] List<Gw2BagEntry?>? Bags);

    private sealed record Gw2BagEntry(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("inventory")] List<Gw2InventorySlot?>? Inventory);

    private sealed record Gw2InventorySlot(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("count")] long Count,
        [property: JsonPropertyName("binding")] string? Binding);
}
