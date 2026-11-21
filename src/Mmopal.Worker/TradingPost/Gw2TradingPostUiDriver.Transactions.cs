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
            if (tx.Type != TransactionType.Buy ||
                string.IsNullOrWhiteSpace(tx.ItemName) ||
                tx.MinimumBuyPriceCopper is not { } topBid ||
                topBid <= tx.PriceCopper)
            {
                continue;
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
        var outbidBuys = SelectSafeOutbidCancels(transactions, maxCancelsPerPass, nowUtc, out var ambiguousNames, out var keptYoung);
        if (keptYoung > 0)
        {
            _logger.LogInformation(
                "Outbid Refresh kept {Count} outbid buy order(s) younger than {Minutes} min so they can still fill.",
                keptYoung,
                (int)MinOrderAgeBeforeOutbidCancel.TotalMinutes);
        }

        foreach (var name in ambiguousNames)
        {
            _logger.LogInformation(
                "Outbid Refresh skipped {Item}: searching it would list more than one of your buy orders, so the first Cancel button might not be the outbid one.",
                name);
        }

        if (outbidBuys.Length == 0)
        {
            return 0;
        }

        var txTab = await WaitForTemplateAsync("transactions.png", TransactionsTabRegion, 0.72m, _uiWaitMs, cancellationToken).ConfigureAwait(false)
                    ?? await TryFindOnceAsync("transactions.png", TopHeaderRegion, 0.75m, cancellationToken).ConfigureAwait(false);
        if (txTab is null)
        {
            return 0;
        }

        await ClickCenterAsync(txTab, cancellationToken).ConfigureAwait(false);
        await SettleAsync(cancellationToken).ConfigureAwait(false);
        await ResetFiltersAsync(cancellationToken).ConfigureAwait(false);

        var currentBtn = await WaitForTemplateAsync("transactions_current.png", CaptureRegion.FullScreen1024x768, 0.75m, _uiWaitMs, cancellationToken).ConfigureAwait(false);
        if (currentBtn is not null)
        {
            await ClickCenterAsync(currentBtn, cancellationToken).ConfigureAwait(false);
            await SettleAsync(cancellationToken).ConfigureAwait(false);
        }

        if (await TryFindOnceAsync("at_transactions_buying.png", CaptureRegion.FullScreen1024x768, 0.75m, cancellationToken).ConfigureAwait(false) is null)
        {
            var buyingBtn = await WaitForTemplateAsync("transactions_buying.png", CaptureRegion.FullScreen1024x768, 0.75m, _uiWaitMs, cancellationToken).ConfigureAwait(false);
            if (buyingBtn is not null)
            {
                await ClickCenterAsync(buyingBtn, cancellationToken).ConfigureAwait(false);
                await SettleAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        var canceled = 0;
        var canceledNames = new List<string>();
        foreach (var order in outbidBuys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsPaused) break;
            await DismissTransientPopupAsync("Outbid Refresh", cancellationToken).ConfigureAwait(false);
            if (!await SearchItemAsync(order.ItemName, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            if (IsPaused) break;
            await SettleAsync(cancellationToken).ConfigureAwait(false);
            var cancelBtn = await WaitForTemplateAsync("transactions_cancel.png", TopHeaderRegion, 0.75m, _uiWaitMs, cancellationToken).ConfigureAwait(false)
                            ?? await TryFindOnceAsync("cancel.png", TopHeaderRegion, 0.75m, cancellationToken).ConfigureAwait(false);
            if (cancelBtn is null)
            {
                continue;
            }

            if (IsPaused) break;
            await ClickCenterAsync(cancelBtn, cancellationToken).ConfigureAwait(false);
            await SettleShortAsync(cancellationToken).ConfigureAwait(false);

            var confirmBtn = await WaitForTemplateAsync("transactions_confirm.png", TopHeaderRegion, 0.75m, _uiWaitMs, cancellationToken).ConfigureAwait(false)
                             ?? await TryFindOnceAsync("confirm.png", TopHeaderRegion, 0.75m, cancellationToken).ConfigureAwait(false);
            if (confirmBtn is not null && !IsPaused)
            {
                await ClickCenterAsync(confirmBtn, cancellationToken).ConfigureAwait(false);
                await SettleAsync(cancellationToken).ConfigureAwait(false);
                canceled++;
                canceledNames.Add(order.ItemName);
                _logger.LogInformation(
                    "Outbid Refresh canceled buy order for {Item}: our bid {OurBid}c, API top bid {TopBid}c.",
                    order.ItemName,
                    order.PriceCopper,
                    order.MinimumBuyPriceCopper);
            }
        }

        LastCanceledOutbidItems = canceledNames;
        return canceled;
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

        var txTab = await WaitForTemplateAsync("transactions.png", TransactionsTabRegion, 0.72m, _uiWaitMs, cancellationToken).ConfigureAwait(false)
                    ?? await TryFindOnceAsync("transactions.png", TopHeaderRegion, 0.75m, cancellationToken).ConfigureAwait(false);
        if (txTab is null)
        {
            return canceledNames;
        }

        await ClickCenterAsync(txTab, cancellationToken).ConfigureAwait(false);
        await SettleAsync(cancellationToken).ConfigureAwait(false);
        await ResetFiltersAsync(cancellationToken).ConfigureAwait(false);
        var currentBtn = await WaitForTemplateAsync("transactions_current.png", CaptureRegion.FullScreen1024x768, 0.75m, _uiWaitMs, cancellationToken).ConfigureAwait(false);
        if (currentBtn is not null)
        {
            await ClickCenterAsync(currentBtn, cancellationToken).ConfigureAwait(false);
            await SettleAsync(cancellationToken).ConfigureAwait(false);
        }

        var sellingBtn = await WaitForTemplateAsync("transactions_selling.png", CaptureRegion.FullScreen1024x768, 0.75m, _uiWaitMs, cancellationToken).ConfigureAwait(false);
        if (sellingBtn is null)
        {
            _logger.LogInformation("Relist skipped: the Selling list of Current transactions was not found.");
            return canceledNames;
        }

        await ClickCenterAsync(sellingBtn, cancellationToken).ConfigureAwait(false);
        await SettleAsync(cancellationToken).ConfigureAwait(false);

        foreach (var relist in relists)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsPaused) break;
            await DismissTransientPopupAsync("Relist", cancellationToken).ConfigureAwait(false);
            if (!await SearchItemAsync(relist.Listing.ItemName, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            await SettleAsync(cancellationToken).ConfigureAwait(false);
            var cancelBtn = await WaitForTemplateAsync("transactions_cancel.png", TopHeaderRegion, 0.75m, _uiWaitMs, cancellationToken).ConfigureAwait(false)
                            ?? await TryFindOnceAsync("cancel.png", TopHeaderRegion, 0.75m, cancellationToken).ConfigureAwait(false);
            if (cancelBtn is null || IsPaused)
            {
                continue;
            }

            await ClickCenterAsync(cancelBtn, cancellationToken).ConfigureAwait(false);
            await SettleShortAsync(cancellationToken).ConfigureAwait(false);
            var confirmBtn = await WaitForTemplateAsync("transactions_confirm.png", TopHeaderRegion, 0.75m, _uiWaitMs, cancellationToken).ConfigureAwait(false)
                             ?? await TryFindOnceAsync("confirm.png", TopHeaderRegion, 0.75m, cancellationToken).ConfigureAwait(false);
            if (confirmBtn is null || IsPaused)
            {
                continue;
            }

            await ClickCenterAsync(confirmBtn, cancellationToken).ConfigureAwait(false);
            await SettleAsync(cancellationToken).ConfigureAwait(false);
            canceledNames.Add(relist.Listing.ItemName);
            _logger.LogInformation("Relist: cancelled the undercut listing for {Item} ({Reason}).", relist.Listing.ItemName, relist.Reason);
        }

        return canceledNames;
    }
}
