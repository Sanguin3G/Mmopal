using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
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
}
