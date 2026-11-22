using System.Net;
using Mmopal.Contracts;
using Mmopal.Infrastructure;

namespace Mmopal.Gw2;

/// <summary>Account data: trade history, Delivery Box, bags, recent buy fills, active orders and the full portfolio sync.</summary>
public sealed partial class Gw2OfficialDataProvider
{
    public async Task<IReadOnlyList<Gw2LedgerTransaction>> GetHistoryTransactionsAsync(
        TradingLedgerStore? itemCache = null,
        CancellationToken cancellationToken = default)
    {
        RequireKey();
        var (buys, sells) = await GetBuysAndSellsAsync<Gw2HistoryTransaction>("v2/commerce/transactions/history", cancellationToken).ConfigureAwait(false);
        var itemIds = DistinctItemIds(buys, sells, row => row.ItemId);
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
            var granted = _cachedPermissions;
            if (granted is null)
            {
                var tokenInfo = await GetAsync<Gw2TokenInfo>("v2/tokeninfo", cancellationToken).ConfigureAwait(false);
                granted = CachePermissions(tokenInfo.Permissions);
            }

            if (!HasInventoryScopes(granted))
            {
                return MarkInventoryScopeUnavailable();
            }

            var characters = await GetAsync<List<Gw2CharacterEntry?>>("v2/characters?ids=all", cancellationToken).ConfigureAwait(false);
            LastCharacterBagItemIds = characters
                .Where(character => !string.IsNullOrWhiteSpace(character?.Name))
                .GroupBy(character => character!.Name!.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyDictionary<int, long>)UnboundItemCounts(group
                        .SelectMany(character => character!.Bags ?? [])
                        .SelectMany(bag => bag?.Inventory ?? [])),
                    StringComparer.OrdinalIgnoreCase);
            var itemIds = UnboundItemIds(characters
                .SelectMany(character => character?.Bags ?? [])
                .SelectMany(bag => bag?.Inventory ?? []));

            // Shared inventory slots (best-effort; do not fail if omitted by mock/proxy).
            try
            {
                var shared = await GetAsync<List<Gw2InventorySlot?>>("v2/account/inventory", cancellationToken).ConfigureAwait(false);
                itemIds.UnionWith(UnboundItemIds(shared));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Character bags already succeeded; ignore optional shared-slot fetch failures.
            }

            return (true, itemIds);
        }
        catch (Exception ex) when (ex is HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized }
                                       or InvalidOperationException)
        {
            return MarkInventoryScopeUnavailable();
        }
    }

    /// <summary>
    /// Unbound item counts per character from the last <see cref="GetUnboundInventoryItemIdsAsync"/> read. Bags
    /// belong to one character and only the logged-in one can list, so this tells which character to swap to.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<int, long>> LastCharacterBagItemIds { get; private set; } =
        new Dictionary<string, IReadOnlyDictionary<int, long>>(StringComparer.OrdinalIgnoreCase);

    private static Dictionary<int, long> UnboundItemCounts(IEnumerable<Gw2InventorySlot?> slots) =>
        slots
            .Where(slot => slot is { Id: > 0, Count: > 0 } && string.IsNullOrWhiteSpace(slot.Binding))
            .GroupBy(slot => slot!.Id)
            .ToDictionary(group => group.Key, group => group.Sum(slot => slot!.Count));

    private (bool Available, IReadOnlySet<int> ItemIds) MarkInventoryScopeUnavailable()
    {
        _inventoryScopeUnavailable = true;
        return (false, new HashSet<int>());
    }

    private static HashSet<int> UnboundItemIds(IEnumerable<Gw2InventorySlot?> slots) =>
        slots
            .Where(slot => slot is { Id: > 0, Count: > 0 } && string.IsNullOrWhiteSpace(slot.Binding))
            .Select(slot => slot!.Id)
            .ToHashSet();

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

    /// <summary>
    /// The newest page (200 rows) of buy and of sell history, with names. The synced ledger lags up to 45 minutes; this
    /// keeps "how many units are Mmopal's" current between syncs.
    /// </summary>
    public async Task<IReadOnlyList<Gw2LedgerTransaction>> GetRecentHistoryAsync(
        TradingLedgerStore? itemCache = null,
        CancellationToken cancellationToken = default)
    {
        RequireKey();
        var (buys, _) = await GetPageAsync<Gw2HistoryTransaction>("v2/commerce/transactions/history/buys", 0, cancellationToken).ConfigureAwait(false);
        var (sells, _) = await GetPageAsync<Gw2HistoryTransaction>("v2/commerce/transactions/history/sells", 0, cancellationToken).ConfigureAwait(false);
        var itemMap = await ResolveItemMetadataAsync(DistinctItemIds(buys, sells, row => row.ItemId), itemCache, cancellationToken).ConfigureAwait(false);
        return buys.Select(row => ToLedgerTransaction(TransactionType.Buy, row, itemMap))
            .Concat(sells.Select(row => ToLedgerTransaction(TransactionType.Sell, row, itemMap)))
            .ToArray();
    }

    public async Task<IReadOnlyList<Gw2ActiveOrder>> GetActiveOrdersDetailedAsync(
        TradingLedgerStore? itemCache = null,
        CancellationToken cancellationToken = default)
    {
        RequireKey();
        var (buys, sells) = await GetBuysAndSellsAsync<Gw2CurrentTransaction>(CurrentTransactionsPath, cancellationToken).ConfigureAwait(false);
        var itemIds = DistinctItemIds(buys, sells, row => row.ItemId);
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

        var deliveryItemsCopper = await ValueDeliveryItemsAsync(delivery, cancellationToken).ConfigureAwait(false);

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

    /// <summary>Values Delivery Box items at the highest bid, or the lowest ask when there is no bid.</summary>
    private async Task<long> ValueDeliveryItemsAsync(Gw2DeliverySnapshot delivery, CancellationToken cancellationToken)
    {
        if (delivery.Items.Count == 0)
        {
            return 0;
        }

        var deliveryIds = delivery.Items.Select(item => item.ItemId).Distinct().ToArray();
        var deliveryPrices = await ReadListingPricesAsync(deliveryIds, cancellationToken).ConfigureAwait(false);
        long totalCopper = 0;
        foreach (var entry in delivery.Items)
        {
            if (!deliveryPrices.TryGetValue(entry.ItemId, out var quote)) continue;
            var unitValue = PositiveUnitPrice(quote.Buys) ?? PositiveUnitPrice(quote.Sells) ?? 0;
            totalCopper += unitValue * entry.Count;
        }

        return totalCopper;
    }

    private static string ItemNameOrPlaceholder(IReadOnlyDictionary<int, Gw2CachedItemInfo> itemMap, int itemId) =>
        itemMap.TryGetValue(itemId, out var info) ? info.Name : $"Item #{itemId}";

    private static Gw2LedgerTransaction ToLedgerTransaction(
        TransactionType kind,
        Gw2HistoryTransaction row,
        IReadOnlyDictionary<int, Gw2CachedItemInfo> itemMap)
    {
        return new Gw2LedgerTransaction(
            Id: row.Id,
            Kind: kind,
            ItemId: row.ItemId,
            ItemName: ItemNameOrPlaceholder(itemMap, row.ItemId),
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
        listings.TryGetValue(row.ItemId, out var listing);
        var highestBuy = PositiveUnitPrice(listing?.Buys);
        var lowestSell = PositiveUnitPrice(listing?.Sells);

        return new Gw2ActiveOrder(
            Id: row.Id,
            Kind: kind,
            ItemId: row.ItemId,
            ItemName: ItemNameOrPlaceholder(itemMap, row.ItemId),
            UnitPriceCopper: row.Price,
            Quantity: row.Quantity,
            CreatedAt: row.Created,
            MarketHighestBuyCopper: highestBuy,
            MarketLowestSellCopper: lowestSell,
            BookStatus: BookStatus(kind, row.Price, highestBuy, lowestSell));
    }

    private static string BookStatus(TransactionType kind, long price, long? highestBuy, long? lowestSell)
    {
        if (kind == TransactionType.Buy)
        {
            if (highestBuy is null) return "Unknown";
            return price >= highestBuy.Value ? "Top of Book" : "Outbid";
        }

        if (lowestSell is null) return "Unknown";
        return price <= lowestSell.Value ? "Top of Book" : "Undercut";
    }
}
