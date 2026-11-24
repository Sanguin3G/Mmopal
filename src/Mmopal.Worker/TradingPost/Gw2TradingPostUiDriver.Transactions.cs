using Microsoft.Extensions.Logging;
using Mmopal.Contracts;
using Mmopal.Gw2;
using Mmopal.Native;
using Mmopal.Worker.Vision;

namespace Mmopal.Worker;

/// <summary>My Transactions: cancelling outbid buy orders and stale undercut listings.</summary>
public sealed partial class Gw2TradingPostUiDriver
{
    /// <summary>
    /// Picks outbid buy orders that can be canceled safely. The cancel flow searches the item name in
    /// My Transactions and clicks the first Cancel button, so an order is skipped whenever that search would
    /// list a buy order that must stay (a duplicate order for the same item, or another item whose name contains
    /// this one, that is the player's, still at the top bid or young) — the click could cancel that one instead.
    /// <paramref name="mayCancel"/> is true for Mmopal's own orders; <paramref name="givenUp"/> for items whose cancel
    /// search cannot be confirmed, which would otherwise fill the per-pass slots every cycle (21 such items stopped
    /// Outbid Refresh altogether on 22-23 Nov).
    /// Orders younger than <see cref="MinOrderAgeBeforeOutbidCancel"/> are kept: competing bots undercut by a few
    /// copper within minutes, and cancelling on every undercut meant no buy order ever stayed up long enough to fill.
    /// </summary>
    internal static Transaction[] SelectSafeOutbidCancels(
        IReadOnlyList<Transaction> transactions,
        int maxCancelsPerPass,
        DateTimeOffset nowUtc,
        out IReadOnlyList<string> ambiguousNames,
        out int keptYoungCount,
        Func<Transaction, bool>? mayCancel = null,
        Func<string, bool>? givenUp = null,
        TimeSpan? minAge = null)
    {
        var buys = transactions
            .Where(tx => tx.Type == TransactionType.Buy && !string.IsNullOrWhiteSpace(tx.ItemName))
            .ToArray();
        bool IsDue(Transaction tx) =>
            (tx.MinimumBuyPriceCopper is { } topBid && topBid > tx.PriceCopper) || nowUtc - tx.Created >= StaleBuyOrderAge;
        bool IsOldEnough(Transaction tx) => nowUtc - tx.Created >= (minAge ?? MinOrderAgeBeforeOutbidCancel);
        bool IsOurs(Transaction tx) => mayCancel?.Invoke(tx) ?? true;
        var skipped = new List<string>();
        var safe = new List<Transaction>();
        keptYoungCount = 0;
        foreach (var tx in buys)
        {
            if (safe.Count >= maxCancelsPerPass) break;
            if (!IsOurs(tx) || !IsDue(tx))
            {
                continue; // the player's order, or at the top bid and recent enough to fill
            }

            if (!IsOldEnough(tx))
            {
                keptYoungCount++;
                continue;
            }

            var name = tx.ItemName.Trim();
            if (givenUp?.Invoke(name) == true)
            {
                continue;
            }

            if (buys.Any(other => other.ItemName.Contains(name, StringComparison.OrdinalIgnoreCase) && !(IsOurs(other) && IsDue(other) && IsOldEnough(other))))
            {
                if (!skipped.Contains(name, StringComparer.OrdinalIgnoreCase)) skipped.Add(name);
                continue;
            }

            safe.Add(tx);
        }

        ambiguousNames = skipped;
        return safe.ToArray();
    }

    /// <summary>The cancel search for this item in that list was not confirmed on screen twice; left alone for an hour.</summary>
    public bool IsCancelSearchGivenUp(TransactionType list, string itemName) =>
        _unconfirmedCancelSearches.TryGetValue($"{list}|{itemName.Trim()}", out var seen) &&
        seen.Count >= UnconfirmedCancelSearchesBeforeSkip &&
        _utcNow() - seen.At < CancelSearchRetryAfter;

    private bool CancelSearchGivenUp(string itemName) => IsCancelSearchGivenUp(_cancelListType, itemName);

    /// <summary>Counts a cancel search that found nothing to cancel; returns how often that happened lately.</summary>
    private int NoteUnconfirmedCancel(string itemName, int weight)
    {
        var key = $"{_cancelListType}|{itemName.Trim()}";
        var earlier = _unconfirmedCancelSearches.TryGetValue(key, out var seen) && _utcNow() - seen.At < CancelSearchRetryAfter ? seen.Count : 0;
        _unconfirmedCancelSearches[key] = (earlier + weight, _utcNow());
        return earlier + weight;
    }

    public async Task<int> CancelOutbidBuyOrdersAsync(
        IReadOnlyList<Transaction> transactions,
        int maxCancelsPerPass,
        Func<Transaction, bool>? mayCancel = null,
        CancellationToken cancellationToken = default,
        bool goldCapReached = false)
    {
        // Reset first: a pass that returns early (reset window) must not report the previous pass's cancels again.
        LastCanceledOutbidItems = Array.Empty<string>();
        LastCanceledStaleItems = Array.Empty<string>();
        if (maxCancelsPerPass <= 0 || transactions.Count == 0)
        {
            return 0;
        }

        _cancelListType = TransactionType.Buy;

        // Only buy orders are canceled when outbid (canceling buys is 100% free; canceling sells burns 5% fee).
        var nowUtc = _utcNow();
        var minAge = MinOrderAgeBeforeOutbidCancel;
        if (Gw2MarketCycle.Describe(nowUtc).PhaseName == "Daily Reset Spike")
        {
            // The reset window is when sellers dump into buy orders; keep every order standing to catch those fills.
            // Unless the gold cap is reached: then orders outbid for an hour are gold that nothing can be bought with
            // (39 of 60 cycles bought nothing in the window of 24 Nov, with 31-53 of 85 orders outbid).
            if (!goldCapReached)
            {
                _logger.LogDebug("Outbid Refresh paused during the daily reset window.");
                return 0;
            }

            minAge = ResetWindowCancelAge;
        }

        var outbidBuys = SelectSafeOutbidCancels(transactions, maxCancelsPerPass, nowUtc, out var ambiguousNames, out var keptYoung, mayCancel, CancelSearchGivenUp, minAge);
        if (keptYoung > 0)
        {
            _logger.LogInformation(
                "Outbid Refresh kept {Count} outbid buy order(s) younger than {Minutes} min so they can still fill.",
                keptYoung,
                (int)minAge.TotalMinutes);
        }

        // Once per item per session: Slice of Allspice Cake repeated this line in 143 of 272 cycles.
        foreach (var name in ambiguousNames.Where(name => _ambiguousOutbidLogged.Add(name)))
        {
            _logger.LogInformation(
                "Outbid Refresh skips {Item} for now: searching it would also list a buy order that stays, so the first Cancel button might not be the outbid one.",
                name);
        }

        if (outbidBuys.Length == 0 || !await ReadyTradingPostAsync("Outbid Refresh", cancellationToken).ConfigureAwait(false))
        {
            return 0;
        }

        if (!await OpenCurrentTransactionsAsync(cancellationToken).ConfigureAwait(false))
        {
            return 0;
        }

        if (await TryFindOnceAsync("at_transactions_buying.png", CaptureRegion.FullScreen1024x768, 0.75m, cancellationToken).ConfigureAwait(false) is null)
        {
            var buyingBtn = await WaitForTemplateAsync("transactions_buying.png", CaptureRegion.FullScreen1024x768, 0.75m, _uiWaitMs, cancellationToken).ConfigureAwait(false);
            if (buyingBtn is not null)
            {
                await ClickCenterAndSettleAsync(buyingBtn, cancellationToken).ConfigureAwait(false);
            }
        }

        _cancelListNames = OwnNames(transactions, TransactionType.Buy);
        var canceledNames = new List<string>();
        var staleNames = new List<string>();
        foreach (var order in outbidBuys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsPaused) break;
            await DismissTransientPopupAsync("Outbid Refresh", cancellationToken).ConfigureAwait(false);
            if (!await SearchItemAsync(order.ItemName, cancellationToken, requireListedName: true).ConfigureAwait(false))
            {
                continue;
            }

            if (IsPaused) break;
            await SettleAsync(cancellationToken).ConfigureAwait(false);
            var cancelBtn = await FindTransactionCancelButtonAsync(cancellationToken).ConfigureAwait(false);
            if (cancelBtn is null)
            {
                continue;
            }

            if (IsPaused) break;
            await ClickCenterAsync(cancelBtn, cancellationToken).ConfigureAwait(false);
            await SettleShortAsync(cancellationToken).ConfigureAwait(false);

            var confirmBtn = await FindTransactionConfirmButtonAsync(cancellationToken).ConfigureAwait(false);
            if (confirmBtn is not null && !IsPaused)
            {
                await ClickCenterAndSettleAsync(confirmBtn, cancellationToken).ConfigureAwait(false);
                if (order.MinimumBuyPriceCopper is { } top && top > order.PriceCopper)
                {
                    canceledNames.Add(order.ItemName);
                    _logger.LogInformation(
                        "Outbid Refresh canceled buy order for {Item}: our bid {OurBid}c, API top bid {TopBid}c.",
                        order.ItemName,
                        order.PriceCopper,
                        order.MinimumBuyPriceCopper);
                }
                else
                {
                    staleNames.Add(order.ItemName);
                    _logger.LogInformation(
                        "Outbid Refresh canceled the {Hours} h old buy order for {Item} ({Qty} left at {OurBid}c, still the top bid but not filling); its gold goes to items that fill.",
                        (int)(nowUtc - order.Created).TotalHours,
                        order.ItemName,
                        order.Quantity,
                        order.PriceCopper);
                }
            }
        }

        _cancelListNames = null;
        LastCanceledOutbidItems = canceledNames.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        LastCanceledStaleItems = staleNames;
        return canceledNames.Count + staleNames.Count;
    }

    /// <summary>
    /// Cancels the given sell listings (Transactions → Current → Selling, search, Cancel, Confirm). The items return
    /// to the Delivery Box, and the sell pass relists them at the typed price with the break-even floor. Returns the
    /// names that were cancelled.
    /// </summary>
    public async Task<IReadOnlyList<string>> CancelSellListingsAsync(
        IReadOnlyList<SellRelist> relists,
        IReadOnlyList<Transaction> orders,
        CancellationToken cancellationToken = default)
    {
        var canceledNames = new List<string>();
        if (relists.Count == 0 || !await ReadyTradingPostAsync("relist", cancellationToken).ConfigureAwait(false))
        {
            return canceledNames;
        }

        if (!await OpenCurrentTransactionsAsync(cancellationToken).ConfigureAwait(false))
        {
            return canceledNames;
        }

        var sellingBtn = await WaitForTemplateAsync("transactions_selling.png", CaptureRegion.FullScreen1024x768, 0.75m, _uiWaitMs, cancellationToken).ConfigureAwait(false);
        if (sellingBtn is null)
        {
            _logger.LogInformation("Relist skipped: the Selling list of Current transactions was not found.");
            return canceledNames;
        }

        await ClickCenterAndSettleAsync(sellingBtn, cancellationToken).ConfigureAwait(false);
        _cancelListType = TransactionType.Sell;
        _cancelListNames = OwnNames(orders, TransactionType.Sell);

        foreach (var relist in relists)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsPaused) break;
            await DismissTransientPopupAsync("Relist", cancellationToken).ConfigureAwait(false);
            if (!await SearchItemAsync(relist.Listing.ItemName, cancellationToken, requireListedName: true).ConfigureAwait(false))
            {
                continue;
            }

            await SettleAsync(cancellationToken).ConfigureAwait(false);
            var cancelBtn = await FindTransactionCancelButtonAsync(cancellationToken).ConfigureAwait(false);
            if (cancelBtn is null || IsPaused)
            {
                continue;
            }

            await ClickCenterAsync(cancelBtn, cancellationToken).ConfigureAwait(false);
            await SettleShortAsync(cancellationToken).ConfigureAwait(false);
            var confirmBtn = await FindTransactionConfirmButtonAsync(cancellationToken).ConfigureAwait(false);
            if (confirmBtn is null || IsPaused)
            {
                continue;
            }

            await ClickCenterAndSettleAsync(confirmBtn, cancellationToken).ConfigureAwait(false);
            canceledNames.Add(relist.Listing.ItemName);
            _logger.LogInformation("Relist: cancelled the undercut listing for {Item} ({Reason}).", relist.Listing.ItemName, relist.Reason);
        }

        _cancelListNames = null;
        return canceledNames;
    }

    private static string[] OwnNames(IEnumerable<Transaction> orders, TransactionType type) =>
        orders
            .Where(order => order.Type == type && !string.IsNullOrWhiteSpace(order.ItemName))
            .Select(order => NormalizeItemName(order.ItemName))
            .Distinct()
            .ToArray();

    /// <summary>
    /// Opens My Transactions (clears its filters) and its Current list when that button is found. False when the
    /// Transactions tab itself was not found.
    /// </summary>
    private async Task<bool> OpenCurrentTransactionsAsync(CancellationToken cancellationToken)
    {
        var txTab = await WaitForTemplateAsync("transactions.png", TransactionsTabRegion, 0.72m, _uiWaitMs, cancellationToken).ConfigureAwait(false)
                    ?? await TryFindOnceAsync("transactions.png", TopHeaderRegion, 0.75m, cancellationToken).ConfigureAwait(false);
        if (txTab is null)
        {
            return false;
        }

        await ClickCenterAndSettleAsync(txTab, cancellationToken).ConfigureAwait(false);
        await ResetFiltersAsync(cancellationToken).ConfigureAwait(false);

        var currentBtn = await WaitForTemplateAsync("transactions_current.png", CaptureRegion.FullScreen1024x768, 0.75m, _uiWaitMs, cancellationToken).ConfigureAwait(false);
        if (currentBtn is not null)
        {
            await ClickCenterAndSettleAsync(currentBtn, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    private async Task<TemplateMatch?> FindTransactionCancelButtonAsync(CancellationToken cancellationToken) =>
        await WaitForTemplateAsync("transactions_cancel.png", TopHeaderRegion, 0.75m, _uiWaitMs, cancellationToken).ConfigureAwait(false)
        ?? await TryFindOnceAsync("cancel.png", TopHeaderRegion, 0.75m, cancellationToken).ConfigureAwait(false);

    private async Task<TemplateMatch?> FindTransactionConfirmButtonAsync(CancellationToken cancellationToken) =>
        await WaitForTemplateAsync("transactions_confirm.png", TopHeaderRegion, 0.75m, _uiWaitMs, cancellationToken).ConfigureAwait(false)
        ?? await TryFindOnceAsync("confirm.png", TopHeaderRegion, 0.75m, cancellationToken).ConfigureAwait(false);
}
