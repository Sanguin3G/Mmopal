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
    /// list more than one of our buy orders (a duplicate order for the same item, or another item whose name
    /// contains this one) — otherwise the click could cancel the wrong order.
    /// Orders younger than <see cref="MinOrderAgeBeforeOutbidCancel"/> are kept: competing bots undercut by a few
    /// copper within minutes, and cancelling on every undercut meant no buy order ever stayed up long enough to fill.
    /// </summary>
    internal static Transaction[] SelectSafeOutbidCancels(
        IReadOnlyList<Transaction> transactions,
        int maxCancelsPerPass,
        DateTimeOffset nowUtc,
        out IReadOnlyList<string> ambiguousNames,
        out int keptYoungCount)
    {
        var buyNames = transactions
            .Where(tx => tx.Type == TransactionType.Buy && !string.IsNullOrWhiteSpace(tx.ItemName))
            .Select(tx => tx.ItemName.Trim())
            .ToArray();
        var skipped = new List<string>();
        var safe = new List<Transaction>();
        keptYoungCount = 0;
        foreach (var tx in transactions)
        {
            if (safe.Count >= maxCancelsPerPass) break;
            if (tx.Type != TransactionType.Buy || string.IsNullOrWhiteSpace(tx.ItemName))
            {
                continue;
            }

            var outbid = tx.MinimumBuyPriceCopper is { } topBid && topBid > tx.PriceCopper;
            if (!outbid && nowUtc - tx.Created < StaleBuyOrderAge)
            {
                continue; // at the top bid and recent enough to fill
            }

            if (nowUtc - tx.Created < MinOrderAgeBeforeOutbidCancel)
            {
                keptYoungCount++;
                continue;
            }

            var name = tx.ItemName.Trim();
            var searchHits = buyNames.Count(other => other.Contains(name, StringComparison.OrdinalIgnoreCase));
            if (searchHits > 1)
            {
                if (!skipped.Contains(name, StringComparer.OrdinalIgnoreCase)) skipped.Add(name);
                continue;
            }

            safe.Add(tx);
        }

        ambiguousNames = skipped;
        return safe.ToArray();
    }

    public async Task<int> CancelOutbidBuyOrdersAsync(
        IReadOnlyList<Transaction> transactions,
        int maxCancelsPerPass,
        CancellationToken cancellationToken = default)
    {
        if (maxCancelsPerPass <= 0 || transactions.Count == 0)
        {
            return 0;
        }

        // Only buy orders are canceled when outbid (canceling buys is 100% free; canceling sells burns 5% fee).
        var nowUtc = _utcNow();
        if (Gw2MarketCycle.Describe(nowUtc).PhaseName == "Daily Reset Spike")
        {
            // The reset window is when sellers dump into buy orders; keep every order standing to catch those fills.
            _logger.LogDebug("Outbid Refresh paused during the daily reset window.");
            return 0;
        }

        LastCanceledOutbidItems = Array.Empty<string>();
        LastCanceledStaleItems = Array.Empty<string>();
        var outbidBuys = SelectSafeOutbidCancels(transactions, maxCancelsPerPass, nowUtc, out var ambiguousNames, out var keptYoung);
        if (keptYoung > 0)
        {
            _logger.LogInformation(
                "Outbid Refresh kept {Count} outbid buy order(s) younger than {Minutes} min so they can still fill.",
                keptYoung,
                (int)MinOrderAgeBeforeOutbidCancel.TotalMinutes);
        }

        // Once per item per session: Slice of Allspice Cake repeated this line in 143 of 272 cycles.
        foreach (var name in ambiguousNames.Where(name => _ambiguousOutbidLogged.Add(name)))
        {
            _logger.LogInformation(
                "Outbid Refresh skips {Item} this session: searching it would list more than one of your buy orders, so the first Cancel button might not be the outbid one. Cancel the extra order in game so Outbid Refresh can manage it.",
                name);
        }

        if (outbidBuys.Length == 0)
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

        LastCanceledOutbidItems = canceledNames;
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
        CancellationToken cancellationToken = default)
    {
        var canceledNames = new List<string>();
        if (relists.Count == 0)
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

        return canceledNames;
    }

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
