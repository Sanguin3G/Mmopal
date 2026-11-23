using System.Text.Json;
using Microsoft.Extensions.Logging;
using Mmopal.Contracts;
using Mmopal.Gw2;
using Mmopal.Infrastructure;
using Mmopal.Native;

namespace Mmopal.Worker;

/// <summary>Bought items and coins: Delivery Box collection, the sell queue, break-even pricing, relisting stale listings.</summary>
public sealed partial class WorkerRuntime
{
    private async Task<(bool Collected, long CoinsCollectedCopper, IReadOnlyList<string> CollectedFlipItemNames)> CollectSmartDeliveryAsync(
        TradingSession session,
        Gw2TradingPostUiDriver liveDriver,
        CancellationToken cancellationToken)
    {
        var effectiveBalance = session.Balance;
        var candidateNameSet = session.CandidateNames;
        var blacklistMatcher = session.BlacklistMatcher;
        var pendingSellTracker = session.SellQueue;
        var reportAccumulator = session.Report;
        if (_marketData is Gw2OfficialDataProvider officialData)
        {
            try
            {
                var now = _utcNow();
                var delivery = await officialData.GetDeliveryAsync(cancellationToken).ConfigureAwait(false);
                if (delivery.CoinsCopper <= 0 && delivery.Items.Count == 0)
                {
                    effectiveBalance.ClearDeliveryCleared();
                    _deliveryEmptySinceUtc = null;
                    return (false, 0, Array.Empty<string>());
                }

                if (effectiveBalance.ShouldSkipStaleDeliveryCollection(delivery.CoinsCopper, delivery.Items.Count, now))
                {
                    return (false, 0, Array.Empty<string>());
                }

                var totalItemCount = delivery.Items.Sum(entry => entry.Count);
                _logger.LogInformation(
                    "GW2 Delivery Box ready: {Coins} coins, {Count} item(s) across {Stacks} slot(s); collecting.",
                    MarketScanItem.FormatCoins(delivery.CoinsCopper),
                    totalItemCount,
                    delivery.Items.Count);

                var collectedFlipItems = new List<string>();
                if (delivery.Items.Count > 0)
                {
                    var itemMeta = await officialData.ResolveItemMetadataAsync(
                        delivery.Items.Select(entry => entry.ItemId),
                        _ledgerStore,
                        cancellationToken).ConfigureAwait(false);
                    foreach (var entry in delivery.Items)
                    {
                        if (itemMeta.TryGetValue(entry.ItemId, out var info) &&
                            !string.IsNullOrWhiteSpace(info.Name) &&
                            candidateNameSet.Contains(info.Name.Trim()) &&
                            !blacklistMatcher.IsBlocked(info.Name))
                        {
                            collectedFlipItems.Add(info.Name.Trim());
                        }
                    }
                }

                var apiWalletBaseline = effectiveBalance.LastObservedApiWalletCopper
                    ?? (await officialData.GetBalanceAsync(cancellationToken).ConfigureAwait(false))?.Copper
                    ?? 0;
                var collectedUi = await liveDriver.CollectDeliveryBoxAsync(cancellationToken).ConfigureAwait(false);
                if (collectedUi || !liveDriver.DeliveryLooksEmpty)
                {
                    _deliveryEmptySinceUtc = null;
                }
                else
                {
                    _deliveryEmptySinceUtc ??= now;
                }

                // The API shows a collected box for up to ~5 min. Still "empty" on screen well after that, the box is
                // locked after all.
                var emptyForTooLong = _deliveryEmptySinceUtc is { } emptySince && now - emptySince >= DeliveryEmptyMeansLocked;
                if (!collectedUi && (liveDriver.DeliveryNeedsTraderNpc || emptyForTooLong))
                {
                    collectedUi = await UnlockDeliveryAtTraderAsync(liveDriver, cancellationToken).ConfigureAwait(false);
                }

                if (!collectedUi)
                {
                    RecordEvent(session, "delivery", liveDriver.DeliveryNeedsTraderNpc ? "locked" : liveDriver.DeliveryLooksEmpty ? "already-empty" : "not-collected");
                }

                effectiveBalance.RecordTakeAllAttempt(delivery.CoinsCopper, apiWalletBaseline, collectedUi, now, delivery.Items.Count);
                if (collectedUi)
                {
                    // It collected, so this character is at a trader. Left set, one locked read early on still swapped the
                    // character 30 min later (several of 7 such swaps in the 22-23 Nov run).
                    _deliveryLockedSinceUtc = null;
                    session.Measured.Collected.UnionWith(collectedFlipItems);
                    RecordEvent(
                        session,
                        "delivery",
                        "collected",
                        unitCopper: delivery.CoinsCopper,
                        detail: $"{totalItemCount} item(s)",
                        activity: $"Collected {MarketScanItem.FormatCoins(delivery.CoinsCopper)} and {totalItemCount} item(s) from the Delivery Box");
                    if (collectedFlipItems.Count > 0)
                    {
                        pendingSellTracker.RecordDeliveryPickup(collectedFlipItems, now);
                    }

                    reportAccumulator?.RecordDeliveryPickup(
                        now,
                        delivery.CoinsCopper,
                        delivery.Items.Count,
                        collectedFlipItems.Count > 0
                            ? $"Picked up {MarketScanItem.FormatCoins(delivery.CoinsCopper)} and {string.Join(", ", collectedFlipItems.Take(3))}."
                            : $"Picked up {MarketScanItem.FormatCoins(delivery.CoinsCopper)}.");

                    if (delivery.CoinsCopper > 0)
                    {
                        _logger.LogInformation(
                            "Collected {Coins} via Take All; crediting live balance temporarily if GW2 API wallet cache lags.",
                            MarketScanItem.FormatCoins(delivery.CoinsCopper));
                    }
                }

                return (collectedUi, collectedUi ? delivery.CoinsCopper : 0, collectedFlipItems);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogDebug(exception, "Delivery API check failed; falling back to visual Delivery Box check.");
            }
        }

        var visualCollected = await liveDriver.CollectDeliveryBoxAsync(cancellationToken).ConfigureAwait(false);
        return (visualCollected, 0, Array.Empty<string>());
    }

    /// <summary>
    /// The Delivery Box only pays out when the TP was opened at a Black Lion trader. Reopen it through the NPC at
    /// most once every 10 minutes; otherwise leave the coins for later (buying and selling still work). This never
    /// switches characters: doing that on every locked cycle caused ~12 logouts in 40 minutes and a server kick.
    /// </summary>
    private async Task<bool> UnlockDeliveryAtTraderAsync(Gw2TradingPostUiDriver liveDriver, CancellationToken cancellationToken)
    {
        var now = _utcNow();
        if (now - _lastDeliveryUnlockAttemptUtc < DeliveryUnlockBackoff)
        {
            return false;
        }

        _lastDeliveryUnlockAttemptUtc = now;
        var (reopened, message) = await liveDriver.ReopenTradingPostAtTraderAsync(cancellationToken).ConfigureAwait(false);
        if (!reopened)
        {
            _logger.LogWarning("Could not reopen the Trading Post at the trader: {Message}", message);
            return false;
        }

        var collected = await liveDriver.CollectDeliveryBoxAsync(cancellationToken).ConfigureAwait(false);
        if (collected)
        {
            _deliveryLockedSinceUtc = null;
            _logger.LogInformation("Delivery Box unlocked at the trader and collected.");
        }
        else
        {
            _deliveryLockedSinceUtc ??= now;
            _logger.LogInformation(
                "Delivery Box still locked after reopening at the trader on {Character}; trying again in 10 minutes. Move that character right next to a Black Lion trader (after {Minutes} min locked Mmopal swaps to another character).",
                liveDriver.CurrentCharacterLabel ?? "this character",
                (int)DeliveryStuckBeforeSwap.TotalMinutes);
        }

        return collected;
    }

    /// <summary>
    /// Drops sell targets whose listing would not cover what was paid. The sell pass lists at the lowest ask minus
    /// the undercut; after the 5% listing and 10% exchange fees that must still cover the most Mmopal paid for the
    /// item (its planned buy price; else the shopping list's buy price). Held items stay queued and are rechecked
    /// on later passes, so they list once the market recovers. Unknown cost or quote: listed as before.
    /// </summary>
    /// <summary>Adds the lowest price of our own current listing of each item, so a new unit joins it instead of undercutting it.</summary>
    private static IReadOnlyDictionary<string, SellPricing> WithOwnListings(
        IReadOnlyDictionary<string, SellPricing> pricing,
        IReadOnlyList<Transaction> currentOrders)
    {
        var ownLowest = currentOrders
            .Where(order => order.Type == TransactionType.Sell && !string.IsNullOrWhiteSpace(order.ItemName))
            .GroupBy(order => order.ItemName.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Min(order => order.PriceCopper), StringComparer.OrdinalIgnoreCase);
        return pricing.ToDictionary(
            pair => pair.Key,
            pair => ownLowest.TryGetValue(pair.Key, out var own) ? pair.Value with { OwnLowestListingCopper = own } : pair.Value,
            StringComparer.OrdinalIgnoreCase);
    }

    private async Task<(IReadOnlyList<string> Targets, IReadOnlyDictionary<string, SellPricing> Pricing)> KeepProfitableSellTargetsAsync(
        IReadOnlyList<string> targets,
        IReadOnlyList<ItemCandidate> candidates,
        int undercutMinCopper,
        CancellationToken cancellationToken)
    {
        var pricing = new Dictionary<string, SellPricing>(StringComparer.OrdinalIgnoreCase);
        if (targets.Count == 0 || _marketData is not Gw2OfficialDataProvider officialData || _ledgerStore is null)
        {
            return (targets, pricing);
        }

        try
        {
            var costs = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in candidates)
            {
                if (candidate.BuyPriceCopper is { } buy && buy > 0)
                {
                    costs[candidate.ItemName.Trim()] = buy;
                }
            }

            foreach (var (name, paid) in await _store.GetOrderedItemCostsAsync(_utcNow().AddDays(-30), cancellationToken).ConfigureAwait(false))
            {
                costs[name.Trim()] = Math.Max(paid, costs.GetValueOrDefault(name.Trim()));
            }

            var ids = await _ledgerStore.GetItemIdsByNamesAsync(targets.ToArray(), cancellationToken).ConfigureAwait(false);
            var quotes = ids.Count == 0
                ? new Dictionary<int, Gw2LivePriceQuote>()
                : await officialData.GetLiveMarketQuotesAsync(ids.Values.Distinct().ToArray(), cancellationToken).ConfigureAwait(false);
            var profitability = new ProfitabilityEngine();
            var keep = new List<string>(targets.Count);
            foreach (var name in targets)
            {
                // Typed sell pricing: API lowest ask (resolves the row text, fallback price) and the break-even floor.
                long? apiLowestAsk = ids.TryGetValue(name.Trim(), out var pricedId) && quotes.TryGetValue(pricedId, out var pricedQuote) && pricedQuote.LowestSellCopper > 0
                    ? pricedQuote.LowestSellCopper
                    : null;
                long? breakEven = costs.TryGetValue(name.Trim(), out var knownCost) ? SellPricing.BreakEvenListingPrice(knownCost) : null;
                pricing[name.Trim()] = new SellPricing(apiLowestAsk, breakEven);

                if (!costs.TryGetValue(name.Trim(), out var cost) ||
                    !ids.TryGetValue(name.Trim(), out var id) ||
                    !quotes.TryGetValue(id, out var quote) ||
                    quote.LowestSellCopper <= 0)
                {
                    keep.Add(name);
                    continue;
                }

                var listPrice = Math.Max(1, quote.LowestSellCopper - Math.Max(0, undercutMinCopper));
                var outcome = profitability.Evaluate(cost, listPrice, new ProfitabilityPolicy());
                if (outcome.IsValid && outcome.ProfitCopper >= 0)
                {
                    keep.Add(name);
                    continue;
                }

                if (!_sellHoldLoggedUtc.TryGetValue(name, out var logged) || _utcNow() - logged > TimeSpan.FromMinutes(30))
                {
                    _sellHoldLoggedUtc[name] = _utcNow();
                    _logger.LogInformation(
                        "Holding {Item}: listing at {List} would lose {Loss} after fees against the {Cost} paid; will retry when the price recovers.",
                        name,
                        MarketScanItem.FormatCoins(listPrice),
                        MarketScanItem.FormatCoins(Math.Abs(outcome.ProfitCopper)),
                        MarketScanItem.FormatCoins(cost));
                }
            }

            return (keep, pricing);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Sell profit check skipped for this pass.");
            return (targets, pricing);
        }
    }

    /// <summary>
    /// Early swap when the Delivery Box has been locked for 30+ min (this character is not at a trader, so coins and
    /// items pile up until the next AFK swap, up to 2.5 h). At most once an hour: swapping on every locked cycle once
    /// caused ~12 logouts in 40 minutes and a server kick.
    /// </summary>
    private bool IsDeliveryStuckSwapDue(DateTimeOffset nowUtc, int parkedCharacterCount) =>
        parkedCharacterCount > 1 &&
        _deliveryLockedSinceUtc is { } since &&
        nowUtc - since >= DeliveryStuckBeforeSwap &&
        nowUtc - _lastCharacterSwapUtc >= MinTimeBetweenDeliverySwaps;

    /// <summary>Cancels listings that have stayed undercut 12 h+ when relisting still covers cost and the lost fee.</summary>
    private async Task RelistStaleUndercutSellsAsync(
        TradingSession session,
        Gw2TradingPostUiDriver liveDriver,
        IReadOnlyList<Transaction> orders,
        CancellationToken cancellationToken)
    {
        // Without knowing which units are Mmopal's, a listing the player made could be the one cancelled.
        if (session.HeldByMmopal is not { } held)
        {
            return;
        }

        try
        {
            var now = _utcNow();
            if (!orders.Any(order => order.Type == TransactionType.Sell && now - order.Created >= SellRelistPolicy.MinListingAge))
            {
                return;
            }

            var paid = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, cost) in await _store.GetOrderedItemCostsAsync(now.AddDays(-30), cancellationToken).ConfigureAwait(false))
            {
                paid[name.Trim()] = Math.Max(cost, paid.GetValueOrDefault(name.Trim()));
            }

            // The API shows a new listing up to ~5 min late; until then it could be the one whose Cancel gets clicked.
            var relistable = orders
                .Where(order => !session.SellQueue.WasJustListed(order.ItemName, now) && OwnedStock.ListingsAreMmopals(order.ItemName, held))
                .ToArray();
            var picks = SellRelistPolicy.Select(
                relistable,
                paid,
                session.CandidateNames,
                _relistedAtUtc,
                Math.Max(1, session.Request.UndercutBufferMinCopper),
                now,
                maxPerPass: 2);
            if (picks.Count == 0)
            {
                return;
            }

            var cancelled = await liveDriver.CancelSellListingsAsync(picks, orders, cancellationToken).ConfigureAwait(false);
            RecordUnconfirmedCancels(session, liveDriver);
            foreach (var name in cancelled)
            {
                _relistedAtUtc[name] = now;
                RecordEvent(session, "relist", "cancelled", name, activity: $"Cancelled the undercut listing of {name} to relist it");
                session.Report.RecordTrade(now, "Relist", name, 1, 0, "Canceled", "Undercut listing cancelled for relisting; it returns via the Delivery Box.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("Relist pass skipped ({Message}).", ex.Message);
        }
    }

    /// <summary>
    /// Only the shopping list and items Mmopal itself ordered count as flip items. The synced buy history holds every
    /// TP purchase on the account, including personal ones (an old "Holographic Super Cheese" was being put up for sale).
    /// </summary>
    private async Task<HashSet<string>> FlipItemNamesAsync(TradingSession session, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var allowedFlipNames = new HashSet<string>(session.CandidateNames, StringComparer.OrdinalIgnoreCase);

        // Items Mmopal itself ordered are flip items even after a re-scan drops them from the shopping list and
        // before the buy history is synced; without this, fills from an older list were never listed.
        try
        {
            foreach (var name in await _store.GetOrderedItemNamesAsync(now.AddDays(-30), cancellationToken).ConfigureAwait(false))
            {
                allowedFlipNames.Add(name.Trim());
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not read previously ordered items.");
        }

        return allowedFlipNames;
    }

    /// <summary>
    /// Reads every character's bags and records which flip items are unsold and on which character (stock ranking,
    /// units per item, and the driver's preferred characters for the next Character Select). Returns the flip item
    /// names found, or null when the bags could not be read.
    /// </summary>
    private async Task<string[]?> ReadFlipStockAsync(
        TradingSession session,
        Gw2OfficialDataProvider officialData,
        IReadOnlySet<string> allowedFlipNames,
        CancellationToken cancellationToken)
    {
        var (invAvailable, bagItemIds) = await officialData.GetUnboundInventoryItemIdsAsync(cancellationToken).ConfigureAwait(false);
        if (!invAvailable)
        {
            if (!_warnedInventoryUnavailable)
            {
                _warnedInventoryUnavailable = true;
                _logger.LogWarning("Auto-list cannot read your bags: the active API key needs the 'characters' and 'inventories' permissions. Bought items are only listed right after a Delivery Box pickup.");
            }

            return null;
        }

        Gw2CachedItemInfo[] flipInfos = [];
        if (bagItemIds.Count > 0 && allowedFlipNames.Count > 0)
        {
            var itemMeta = await officialData.ResolveItemMetadataAsync(bagItemIds, _ledgerStore, cancellationToken).ConfigureAwait(false);
            flipInfos = itemMeta.Values
                .Where(info => !string.IsNullOrWhiteSpace(info.Name) &&
                               allowedFlipNames.Contains(info.Name.Trim()) &&
                               !session.BlacklistMatcher.IsBlocked(info.Name))
                .ToArray();
        }

        var flipNamesById = flipInfos.GroupBy(info => info.ItemId).ToDictionary(group => group.Key, group => group.First().Name.Trim());
        session.FlipItemsByCharacter = CharacterStock.ItemsByCharacter(officialData.LastCharacterBagItemIds, flipNamesById);
        session.FlipUnitsByCharacter = officialData.LastCharacterBagItemIds.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyDictionary<string, long>)pair.Value
                .Where(item => flipNamesById.ContainsKey(item.Key))
                .GroupBy(item => flipNamesById[item.Key], StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Sum(item => item.Value), StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
        await ReadHeldByMmopalAsync(session, officialData, cancellationToken).ConfigureAwait(false);
        session.FlipStockByCharacter = CharacterStock.Rank(session.FlipUnitsByCharacter, session.HeldByMmopal);
        session.HeldFlipItems = CharacterStock.HeldByItem(session.FlipUnitsByCharacter, session.HeldByMmopal);
        if (session.LiveDriver is { } driver)
        {
            // A launch, game restart or disconnect lands on Character Select too: enter the character holding stock.
            driver.PreferredCharacterNames = ParkedFlipStock(session, driver).Select(entry => entry.Name).ToArray();
        }

        return flipInfos.Select(info => info.Name.Trim()).ToArray();
    }

    /// <summary>
    /// How many units of each item are Mmopal's own (see <see cref="OwnedStock"/>): its placed buy orders (60 days), the
    /// synced history plus the newest history page, and the current listings. Left unchanged when a read fails.
    /// </summary>
    private async Task ReadHeldByMmopalAsync(TradingSession session, Gw2OfficialDataProvider officialData, CancellationToken cancellationToken)
    {
        try
        {
            var placed = (await _store.GetPlacedBuyOrdersAsync(_utcNow().AddDays(-60), cancellationToken).ConfigureAwait(false))
                .Select(order => new PlacedBuyOrder(order.ItemName, order.PlacedAt));
            var recent = await officialData.GetRecentHistoryAsync(_ledgerStore, cancellationToken).ConfigureAwait(false);
            session.HeldByMmopal = OwnedStock.UnitsHeldByMmopal(placed, session.LedgerHistory.Concat(recent), session.LastKnownActiveOrders);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not work out which units are Mmopal's this cycle; keeping the last count.");
        }
    }

    /// <summary>
    /// Sell targets whose stack on this character is all Mmopal's. The sell dialog lists the whole stack, so an item
    /// where the player holds copies of their own (food, infusions, a legendary on the shopping list) is left alone.
    /// When the current character is unknown, every character's units count. Without a count yet, nothing is dropped.
    /// </summary>
    private IReadOnlyList<string> KeepMmopalStacks(TradingSession session, IReadOnlyList<string> targets, string? currentCharacter)
    {
        if (session.HeldByMmopal is not { } held || targets.Count == 0)
        {
            return targets;
        }

        var here = session.FlipUnitsByCharacter.FirstOrDefault(pair => CharacterStock.NamesMatch(pair.Key, currentCharacter)).Value;
        var kept = new List<string>(targets.Count);
        foreach (var item in targets)
        {
            var stack = here is not null
                ? here.GetValueOrDefault(item)
                : session.FlipUnitsByCharacter.Values.Sum(units => units.GetValueOrDefault(item));
            if (stack == 0 || OwnedStock.StackIsMmopals(item, stack, held))
            {
                kept.Add(item); // not in a bag yet (just collected), or every unit here is Mmopal's
                continue;
            }

            if (session.OwnCopiesLogged.Add(item))
            {
                _logger.LogInformation(
                    "Not listing {Item}: the bags hold {Stack} but only {Mine} of them were bought by Mmopal; the rest are yours, and the sell dialog would list the whole stack.",
                    item,
                    stack,
                    Math.Max(0, held.GetValueOrDefault(ItemNameNormalizer.Normalize(item))));
            }
        }

        return kept;
    }

    /// <summary>
    /// Before the first cycle opens the game: reads where the unsold stock is, so the first Character Select enters
    /// the character holding the most instead of a rule-picked slot and a stock swap minutes later.
    /// </summary>
    private async Task ReadFlipStockBeforeLaunchAsync(TradingSession session, CancellationToken cancellationToken)
    {
        if (session.LiveDriver is null || !session.Request.EnableInventorySellPass || session.Client.ParkedCharacterCount <= 1 ||
            _marketData is not Gw2OfficialDataProvider officialData)
        {
            return;
        }

        try
        {
            var names = await FlipItemNamesAsync(session, _utcNow(), cancellationToken).ConfigureAwait(false);
            if (await ReadFlipStockAsync(session, officialData, names, cancellationToken).ConfigureAwait(false) is not null)
            {
                _logger.LogInformation("Unsold flip items by character before launch: {Stock}.", DescribeFlipStock(session.FlipStockByCharacter));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not read bags before launch; Character Select uses the usual slot choice.");
        }
    }

    /// <summary>Queues bought flip items found in the bags and new buy fills from the API (at most every 2 minutes).</summary>
    private async Task SyncOwnedFlipInventoryFromApiAsync(TradingSession session, CancellationToken cancellationToken)
    {
        if (_marketData is not Gw2OfficialDataProvider officialData)
        {
            return;
        }

        var now = _utcNow();
        if (session.LastInventoryApiSyncUtc != DateTimeOffset.MinValue && (now - session.LastInventoryApiSyncUtc) < TimeSpan.FromMinutes(2))
        {
            return;
        }

        session.LastInventoryApiSyncUtc = now;
        var liveDriver = session.LiveDriver;
        var blacklistMatcher = session.BlacklistMatcher;
        var pendingSellTracker = session.SellQueue;
        var allowedFlipNames = await FlipItemNamesAsync(session, now, cancellationToken).ConfigureAwait(false);

        try
        {
            var confirmedFlipItems = await ReadFlipStockAsync(session, officialData, allowedFlipNames, cancellationToken).ConfigureAwait(false);
            if (confirmedFlipItems is not null)
            {
                pendingSellTracker.SyncFromCharacterInventory(confirmedFlipItems, now);
                pendingSellTracker.DropUnseen(confirmedFlipItems, now);
            }

            if (confirmedFlipItems is { Length: > 0 })
            {
                var seen = string.Join(", ", confirmedFlipItems.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
                if (!string.Equals(seen, _lastLoggedBagFlipItems, StringComparison.Ordinal))
                {
                    _lastLoggedBagFlipItems = seen;
                    _logger.LogInformation("Bought flip items in bags, queued to list: {Items}", seen);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Bag inventory API sync skipped for this cycle.");
        }

        try
        {
            var (highestBuyId, newFills) = await officialData.GetRecentHistoryBuyItemIdsAsync(
                session.LastHistoryBuyId,
                cancellationToken).ConfigureAwait(false);
            session.LastHistoryBuyId = highestBuyId;

            if (newFills.Count > 0 && allowedFlipNames.Count > 0)
            {
                var fillMeta = await officialData.ResolveItemMetadataAsync(
                    newFills.Select(f => f.ItemId),
                    _ledgerStore,
                    cancellationToken).ConfigureAwait(false);
                var filledFlipNames = fillMeta.Values
                    .Where(info => !string.IsNullOrWhiteSpace(info.Name) &&
                                   allowedFlipNames.Contains(info.Name.Trim()) &&
                                   !blacklistMatcher.IsBlocked(info.Name))
                    .Select(info => info.Name.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                _buyCompetition.RecordFills(filledFlipNames);
                if (filledFlipNames.Length > 0)
                {
                    var pickedUp = false;
                    if (liveDriver is not null)
                    {
                        var deliveryResult = await CollectSmartDeliveryAsync(session, liveDriver, cancellationToken).ConfigureAwait(false);
                        pickedUp = deliveryResult.Collected;
                    }

                    if (pickedUp || liveDriver is null)
                    {
                        pendingSellTracker.RecordNewBuyFill(filledFlipNames, now);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Buy history watermark sync skipped for this cycle.");
        }
    }
}
