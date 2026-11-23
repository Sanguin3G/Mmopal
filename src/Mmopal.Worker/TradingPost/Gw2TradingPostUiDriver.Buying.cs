using Microsoft.Extensions.Logging;
using Mmopal.Contracts;
using Mmopal.Gw2;
using Mmopal.Native;
using Mmopal.Worker.Vision;

namespace Mmopal.Worker;

/// <summary>Placing buy orders: price choice (typed or row click), item checks, and the visible-price safety checks.</summary>
public sealed partial class Gw2TradingPostUiDriver
{
    /// <summary>
    /// Decides whether the price showing in the buy dialog is safe to submit. The planner priced the order from
    /// the API (<paramref name="expectedCopper"/> = top bid + overbid) but the dialog is filled by clicking the
    /// in-game order book, so a misclick can silently raise the bid (e.g. onto a seller row) or drop it under the top
    /// bid where it never fills. Only a plausibly real reading aborts; unreadable or wildly-off readings are treated
    /// as OCR misses so an uncalibrated reader can never stop all buying.
    /// </summary>
    internal static (bool Abort, string Message) EvaluateVisibleBuyPrice(long expectedCopper, int overbidMaxCopper, long? visibleCopper, bool confident = true)
    {
        var verdict = EvaluateVisibleBuyPriceCore(expectedCopper, overbidMaxCopper, visibleCopper);
        return verdict.Abort && !confident
            ? (false, $"Possible {verdict.Message} Only the price boxes were readable (their spinners garble digits), so placing anyway.")
            : verdict;
    }

    private static (bool Abort, string Message) EvaluateVisibleBuyPriceCore(long expectedCopper, int overbidMaxCopper, long? visibleCopper)
    {
        if (expectedCopper <= 0)
        {
            return (false, "No planned price to compare against; placing without a price check.");
        }

        if (visibleCopper is not { } visible || visible <= 0)
        {
            return (false, $"Visible price unreadable; placing at the in-game price (planned {MarketScanItem.FormatCoins(expectedCopper)}).");
        }

        var ceiling = expectedCopper + Math.Max(0, overbidMaxCopper) + Math.Max(5L, expectedCopper * 3 / 100);
        if (visible > expectedCopper * 3 || visible * 3 < expectedCopper)
        {
            return (false, $"Visible price {MarketScanItem.FormatCoins(visible)} is implausible vs planned {MarketScanItem.FormatCoins(expectedCopper)}; treating it as an OCR misread.");
        }

        if (visible > ceiling)
        {
            return (true, $"visible price {MarketScanItem.FormatCoins(visible)} is above the ceiling {MarketScanItem.FormatCoins(ceiling)} (planned {MarketScanItem.FormatCoins(expectedCopper)}).");
        }

        // A bid this far under the top bid will not fill; it only sits there until Outbid Refresh cancels it.
        var floor = expectedCopper * 9 / 10;
        return visible < floor
            ? (true, $"visible price {MarketScanItem.FormatCoins(visible)} is more than 10% under the planned {MarketScanItem.FormatCoins(expectedCopper)}; the order would sit below the top bid and not fill.")
            : (false, $"Visible price {MarketScanItem.FormatCoins(visible)} OK (planned {MarketScanItem.FormatCoins(expectedCopper)}, ceiling {MarketScanItem.FormatCoins(ceiling)}).");
    }

    public async Task<TradingActionResult> PlaceBuyOrderAsync(
        TradingAction action,
        int overbidMinCopper,
        int overbidMaxCopper,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (IsPaused)
        {
            return new TradingActionResult(action, TradingActionStatus.Skipped, "Paused by operator before starting order.");
        }

        var (tpReady, tpMessage) = await EnsureTradingPostOpenAsync(cancellationToken).ConfigureAwait(false);
        if (!tpReady)
        {
            return new TradingActionResult(action, TradingActionStatus.Failed, tpMessage);
        }

        await GoToRestPositionAsync(cancellationToken).ConfigureAwait(false);
        await CloseItemWindowAsync(cancellationToken).ConfigureAwait(false);

        if (!await SelectBuyTabAsync(cancellationToken).ConfigureAwait(false))
        {
            return new TradingActionResult(action, TradingActionStatus.Failed, "Buy Items tab was not detected.");
        }

        await GoToRestPositionAsync(cancellationToken).ConfigureAwait(false);
        await ResetFiltersAsync(cancellationToken).ConfigureAwait(false);

        if (IsPaused)
        {
            return new TradingActionResult(action, TradingActionStatus.Skipped, "Paused by operator before searching item.");
        }

        if (!await SearchItemAsync(action.ItemName, cancellationToken).ConfigureAwait(false))
        {
            return new TradingActionResult(action, TradingActionStatus.Failed, $"Search field not found when looking up '{action.ItemName}'.");
        }

        await SettleAsync(cancellationToken).ConfigureAwait(false);
        await GoToRestPositionAsync(cancellationToken).ConfigureAwait(false);

        // Scoped to the results header: full-screen matching can also hit the Delivery Box "Items:" label (~0.76).
        var firstItem = await WaitForTemplateAsync("item.png", SearchResultsHeaderRegion, 0.74m, _uiWaitMs, cancellationToken).ConfigureAwait(false);
        if (firstItem is null)
        {
            return new TradingActionResult(action, TradingActionStatus.Failed, $"No search result row appeared for '{action.ItemName}'.");
        }

        // The TP search is a substring match, so the first row can be a different item ("Slice of Allspice Cake" also
        // lists "Slice of Allspice Cake with Ice Cream"). Open rows in order and buy only from the one whose dialog
        // title is the exact item.
        TemplateMatch? buyersAnchor = null;
        var wrongTitles = new List<string>();
        var titleUnread = false;
        for (var row = 0; row < MaxSearchRowsToTry; row++)
        {
            if (IsPaused)
            {
                return new TradingActionResult(action, TradingActionStatus.Skipped, "Paused by operator before opening item.");
            }

            var rowAnchor = firstItem with { Y = firstItem.Y + row * SearchResultRowStride };
            buyersAnchor = await OpenItemOrderWindowAsync(rowAnchor, "currentBuyers.png", 0.72m, cancellationToken).ConfigureAwait(false);
            if (buyersAnchor is null)
            {
                break;
            }

            var (titleCheck, seenTitle) = await CheckOpenedItemTitleAsync(action.ItemName, cancellationToken).ConfigureAwait(false);
            if (titleCheck == ItemTitleCheck.Unreadable)
            {
                _logger.LogInformation("Could not read the item window title for {Item} (search row {Row}); it needs the search list and the live price as proof.", action.ItemName, row + 1);
                titleUnread = _textLocator is not null;
                break;
            }

            if (titleCheck == ItemTitleCheck.Match)
            {
                if (row > 0)
                {
                    _logger.LogInformation("Found '{Item}' at search row {Row}.", action.ItemName, row + 1);
                }

                break;
            }

            wrongTitles.Add(seenTitle ?? "?");
            _logger.LogInformation("Search row {Row} for '{Item}' opened '{Other}'; trying the next row.", row + 1, action.ItemName, seenTitle);
            await CloseItemWindowAsync(cancellationToken).ConfigureAwait(false);
            buyersAnchor = null;
        }

        if (buyersAnchor is null)
        {
            return await CloseItemWindowWithResultAsync(
                action,
                TradingActionStatus.Failed,
                wrongTitles.Count > 0
                    ? $"None of the search rows opened '{action.ItemName}' (saw: {string.Join(", ", wrongTitles.Select(t => $"'{t}'"))}); not buying a different item."
                    : $"Item order window did not open for '{action.ItemName}'.",
                cancellationToken).ConfigureAwait(false);
        }

        // Wait for async order-book rows from the GW2 server before selecting Row 1 under Current Buyers.
        await SettleAsync(cancellationToken).ConfigureAwait(false);
        long? liveTopBid = null;
        if (_settings.TypesExactPrice && _textLocator is not null)
        {
            // Read before clicking a row: afterwards our own "Ordering N" preview row joins the list.
            liveTopBid = await ReadTopBuyerPriceAsync(Math.Max(1, action.PriceCopper - overbidMinCopper), cancellationToken).ConfigureAwait(false);
        }

        if (titleUnread && !UnreadTitleIsProven(_lastSearchShowedName, liveTopBid, Math.Max(1, action.PriceCopper - overbidMinCopper)))
        {
            return await CloseItemWindowWithResultAsync(
                action,
                TradingActionStatus.Failed,
                $"Buy order aborted: the item window's title could not be read, and {(liveTopBid is null ? "the live top bid could not be read either" : "the search list or the live top bid does not prove it")}; it may be a different item.",
                cancellationToken).ConfigureAwait(false);
        }

        await _input.ClickAsync(CenterX(buyersAnchor), CenterY(buyersAnchor) + 48, cancellationToken).ConfigureAwait(false);
        await SettleShortAsync(cancellationToken).ConfigureAwait(false);

        if (_requireTemplateFilesOnDisk)
        {
            for (var selectTry = 0; selectTry < 3; selectTry++)
            {
                if (IsPaused)
                {
                    return await CloseItemWindowWithResultAsync(action, TradingActionStatus.Skipped, "Paused by operator before submitting buy order.", cancellationToken).ConfigureAwait(false);
                }

                if (await TryFindOnceAsync("placeOrder.png", CaptureRegion.FullScreen1024x768, 0.72m, cancellationToken).ConfigureAwait(false) is not null)
                {
                    break;
                }

                await SettleAsync(cancellationToken).ConfigureAwait(false);
                if (selectTry == 0)
                {
                    await _input.ClickAsync(CenterX(buyersAnchor), CenterY(buyersAnchor) + 48, cancellationToken).ConfigureAwait(false);
                }
                else if (selectTry == 1)
                {
                    await _input.ClickAsync(Math.Max(10, CenterX(buyersAnchor) - 40), CenterY(buyersAnchor) + 46, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    var downArrow = await FindPriceSpinnerArrowAsync("arrow_down.png", cancellationToken).ConfigureAwait(false);
                    if (downArrow is not null)
                    {
                        await ClickCenterAsync(downArrow, cancellationToken).ConfigureAwait(false);
                    }
                }

                await SettleShortAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        var qty = Math.Clamp(action.Quantity, 1L, 250L);
        if (qty > 1)
        {
            var qtyBox = await WaitForTemplateAsync("quantity.png", CaptureRegion.FullScreen1024x768, 0.68m, _uiWaitMs, cancellationToken).ConfigureAwait(false);
            if (qtyBox is not null)
            {
                await _input.ClickAsync(CenterX(qtyBox) + 112, CenterY(qtyBox), cancellationToken).ConfigureAwait(false);
                await ReplaceFieldTextAsync(qty.ToString(System.Globalization.CultureInfo.InvariantCulture), LongestNumberText, cancellationToken).ConfigureAwait(false);
            }
        }

        var overbidSteps = PickStepCount(overbidMinCopper, overbidMaxCopper);
        long? typedUnitPrice = null;
        if (_settings.TypesExactPrice && _textLocator is not null)
        {
            var choice = ChooseTypedBuyPrice(action.PriceCopper, overbidMinCopper, overbidMaxCopper, overbidSteps, liveTopBid);
            _logger.LogInformation("Typed price for {Item}: {Message}", action.ItemName, choice.Message);
            if (choice.Target is not { } target)
            {
                return await CloseItemWindowWithResultAsync(action, TradingActionStatus.Failed, $"Buy order aborted: {choice.Message}", cancellationToken).ConfigureAwait(false);
            }

            var (confirmed, confirmMessage) = await TypeAndConfirmPriceAsync(target, cancellationToken, qty).ConfigureAwait(false);
            _logger.LogInformation("Buy price check for {Item}: {Message}", action.ItemName, confirmMessage);
            if (!confirmed)
            {
                return await CloseItemWindowWithResultAsync(action, TradingActionStatus.Failed, $"Buy order aborted: {confirmMessage}", cancellationToken).ConfigureAwait(false);
            }

            typedUnitPrice = target;
        }
        else
        {
            for (var i = 0; i < overbidSteps; i++)
            {
                var upArrow = await FindPriceSpinnerArrowAsync("arrow_up.png", cancellationToken).ConfigureAwait(false);
                if (upArrow is null) break;
                await ClickCenterAsync(upArrow, cancellationToken).ConfigureAwait(false);
            }
        }

        if (IsPaused)
        {
            return await CloseItemWindowWithResultAsync(action, TradingActionStatus.Skipped, "Paused by operator before submitting buy order.", cancellationToken).ConfigureAwait(false);
        }

        // A message box over the dialog would swallow the Place Order click while the order is reported as placed.
        await DismissTransientPopupAsync("buy order", cancellationToken).ConfigureAwait(false);
        var placeButton = await WaitForTemplateAsync("placeOrder.png", CaptureRegion.FullScreen1024x768, 0.72m, _uiWaitMs, cancellationToken).ConfigureAwait(false);
        if (placeButton is null)
        {
            return await CloseItemWindowWithResultAsync(action, TradingActionStatus.Failed, $"Place Order button was not visible for '{action.ItemName}'.", cancellationToken).ConfigureAwait(false);
        }

        if (_textLocator is not null && typedUnitPrice is null)
        {
            var (visibleUnitPrice, confident) = await ReadDialogUnitPriceAsync(qty, cancellationToken).ConfigureAwait(false);
            if (visibleUnitPrice is null)
            {
                await SaveDiagnosticFrameAsync("price-unreadable", cancellationToken).ConfigureAwait(false);
            }

            var check = EvaluateVisibleBuyPrice(action.PriceCopper, overbidMaxCopper, visibleUnitPrice, confident);
            _logger.LogInformation("Buy price check for {Item}: {Message}", action.ItemName, check.Message);
            if (check.Abort)
            {
                return await CloseItemWindowWithResultAsync(action, TradingActionStatus.Failed, $"Buy order aborted: {check.Message}", cancellationToken).ConfigureAwait(false);
            }
        }

        await ClickCenterAndSettleAsync(placeButton, cancellationToken).ConfigureAwait(false);
        await CloseItemWindowAsync(cancellationToken).ConfigureAwait(false);

        return typedUnitPrice is { } placed
            ? new TradingActionResult(
                action,
                TradingActionStatus.Completed,
                $"Placed live buy order for {qty}x '{action.ItemName}' at {MarketScanItem.FormatCoins(placed)} (typed).")
            {
                PlacedUnitPriceCopper = placed
            }
            : new TradingActionResult(
                action,
                TradingActionStatus.Completed,
                $"Placed live buy order for {qty}x '{action.ItemName}' (+{overbidSteps}c overbid).");
    }

    private async Task<TradingActionResult> CloseItemWindowWithResultAsync(
        TradingAction action,
        TradingActionStatus status,
        string message,
        CancellationToken cancellationToken)
    {
        await CloseItemWindowAsync(cancellationToken).ConfigureAwait(false);
        return new TradingActionResult(action, status, message);
    }

    /// <summary>
    /// Target unit price for Type exact mode: the live top bid read in the dialog (else the API top bid the plan used)
    /// plus the overbid. Refused when it exceeds the same ceiling the price check uses (planned price + max overbid +
    /// ~3%): the market moved past the planned margin.
    /// </summary>
    internal static (long? Target, string Message) ChooseTypedBuyPrice(
        long plannedCopper,
        int overbidMinCopper,
        int overbidMaxCopper,
        int overbidSteps,
        long? liveTopBidCopper)
    {
        var apiTopBid = Math.Max(1, plannedCopper - Math.Max(0, overbidMinCopper));
        var basis = liveTopBidCopper ?? apiTopBid;
        var target = basis + Math.Max(0, overbidSteps);
        var ceiling = plannedCopper + Math.Max(0, overbidMaxCopper) + Math.Max(5L, plannedCopper * 3 / 100);
        var source = liveTopBidCopper is null
            ? $"API top bid {MarketScanItem.FormatCoins(apiTopBid)} (live top bid unreadable)"
            : $"live top bid {MarketScanItem.FormatCoins(basis)} (API {MarketScanItem.FormatCoins(apiTopBid)})";
        return target > ceiling
            ? (null, $"{source} + {overbidSteps}c = {MarketScanItem.FormatCoins(target)} is above the profitable ceiling {MarketScanItem.FormatCoins(ceiling)}; someone outbid past the margin.")
            : (target, $"{source} + {overbidSteps}c -> typing {MarketScanItem.FormatCoins(target)}.");
    }

    /// <summary>Typed mode: the dialog's Total must show exactly the typed price, else the typing did not stick.</summary>
    internal static (bool Abort, string Message) EvaluateTypedBuyPrice(long typedCopper, long? visibleCopper, bool confident)
    {
        if (visibleCopper is not { } visible)
        {
            return (false, $"Typed {MarketScanItem.FormatCoins(typedCopper)}; the dialog price could not be read to confirm it.");
        }

        if (visible == typedCopper)
        {
            return (false, $"Typed {MarketScanItem.FormatCoins(typedCopper)} confirmed.");
        }

        return confident
            ? (true, $"typed {MarketScanItem.FormatCoins(typedCopper)} but the dialog shows {MarketScanItem.FormatCoins(visible)}; the price boxes did not take the typed value.")
            : (false, $"Typed {MarketScanItem.FormatCoins(typedCopper)}; boxes read {MarketScanItem.FormatCoins(visible)} (unconfirmed, spinners garble that read).");
    }
}
