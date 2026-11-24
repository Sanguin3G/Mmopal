using Microsoft.Extensions.Logging;
using Mmopal.Contracts;
using Mmopal.Gw2;
using Mmopal.Native;
using Mmopal.Worker.Vision;

namespace Mmopal.Worker;

/// <summary>Listing bought items from the bags: title check, typed price with the break-even floor, List This Item.</summary>
public sealed partial class Gw2TradingPostUiDriver
{
    /// <summary>
    /// Refuses unfiltered bag selling so unrelated personal food/consumables in inventory are never sold.
    /// Call the overload with an explicit allow-list of flip item names instead.
    /// </summary>
    public Task<int> SellInventoryPassAsync(
        int maxItemsPerPass,
        int undercutMinCopper,
        int undercutMaxCopper,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(0);

    /// <summary>
    /// Lists ONLY explicit flip items from the character's bags by typing each allowed item name into
    /// Sell Items Search first, checking that it actually exists in bags, and never clicking Sell Instantly.
    /// </summary>
    public async Task<int> SellInventoryPassAsync(
        IReadOnlyList<string> allowedFlipItemNames,
        int maxItemsPerPass,
        int undercutMinCopper,
        int undercutMaxCopper,
        CancellationToken cancellationToken = default)
    {
        var results = await SellInventoryPassDetailedAsync(
            allowedFlipItemNames,
            maxItemsPerPass,
            undercutMinCopper,
            undercutMaxCopper,
            cancellationToken).ConfigureAwait(false);
        return results.Count(r => r.Outcome == SellPassItemOutcome.Listed);
    }

    /// <summary>
    /// Executes the filtered bag sell pass and returns per-item outcomes so the caller can remove listed items
    /// and apply retry backoff to items not yet in the active character's bags.
    /// </summary>
    public async Task<IReadOnlyList<SellPassItemResult>> SellInventoryPassDetailedAsync(
        IReadOnlyList<string> allowedFlipItemNames,
        int maxItemsPerPass,
        int undercutMinCopper,
        int undercutMaxCopper,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, SellPricing>? pricing = null)
    {
        if (maxItemsPerPass <= 0 || allowedFlipItemNames is null || allowedFlipItemNames.Count == 0)
        {
            return Array.Empty<SellPassItemResult>();
        }

        var targets = allowedFlipItemNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(maxItemsPerPass)
            .ToArray();
        if (targets.Length == 0)
        {
            return Array.Empty<SellPassItemResult>();
        }

        var results = new List<SellPassItemResult>(targets.Length);
        if (!await ReadyTradingPostAsync("sell pass", cancellationToken).ConfigureAwait(false))
        {
            return results;
        }

        var sellTab = await TryFindOnceAsync("items_tab.png", SellTabRegion, 0.72m, cancellationToken).ConfigureAwait(false);
        if (sellTab is not null)
        {
            await ClickCenterAsync(sellTab, cancellationToken).ConfigureAwait(false);
        }
        else if (_requireTemplateFilesOnDisk)
        {
            await _input.ClickAsync(DeterministicSellTabPoint.X, DeterministicSellTabPoint.Y, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            return results;
        }

        await SettleAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            foreach (var targetItem in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SellPricing? itemPricing = null;
                pricing?.TryGetValue(targetItem, out itemPricing);
                if (pricing is not null && _settings.TypesExactPrice && itemPricing?.ApiLowestAskCopper is not > 0)
                {
                    // Listing by the arrows checks neither the price nor break-even: four items went up that way while the
                    // API was down, and one held for its loss was tried three times (24 Nov).
                    results.Add(new SellPassItemResult(targetItem, SellPassItemOutcome.SkippedError, "No API price to check the listing against."));
                    continue;
                }

                await DismissTransientPopupAsync("sell pass", cancellationToken).ConfigureAwait(false);
                if (IsPaused || await HasErrorPopupAsync(cancellationToken).ConfigureAwait(false))
                {
                    results.Add(new SellPassItemResult(targetItem, SellPassItemOutcome.SkippedError, "Paused or error popup active."));
                    break;
                }

                if (!await TradingPostProvenAsync(cancellationToken).ConfigureAwait(false))
                {
                    _logger.LogWarning("The Trading Post closed during the sell pass; the rest is listed next cycle.");
                    break;
                }

                await GoToRestPositionAsync(cancellationToken).ConfigureAwait(false);
                await ResetFiltersAsync(cancellationToken).ConfigureAwait(false);

                if (!await SearchItemAsync(targetItem, cancellationToken).ConfigureAwait(false))
                {
                    results.Add(new SellPassItemResult(targetItem, SellPassItemOutcome.SkippedError, "Search input unavailable."));
                    continue;
                }

                await SettleAsync(cancellationToken).ConfigureAwait(false);

                if (await TryFindOnceAsync("no_items.png", CaptureRegion.FullScreen1024x768, 0.75m, cancellationToken).ConfigureAwait(false) is not null)
                {
                    results.Add(new SellPassItemResult(targetItem, SellPassItemOutcome.NotFoundInBags, "Not present in current character bags."));
                    continue;
                }

                var firstItem = await WaitForTemplateAsync("item.png", CaptureRegion.FullScreen1024x768, 0.74m, _uiWaitMs, cancellationToken).ConfigureAwait(false);
                if (firstItem is null)
                {
                    results.Add(new SellPassItemResult(targetItem, SellPassItemOutcome.NotFoundInBags, "No sellable item row appeared."));
                    continue;
                }

                // Bag search is a substring match too: open rows in order and list only the exact item.
                TemplateMatch? sellersAnchor = null;
                var wrongTitles = new List<string>();
                var titleUnread = false;
                for (var row = 0; row < MaxSearchRowsToTry; row++)
                {
                    var rowAnchor = firstItem with { Y = firstItem.Y + row * SellResultRowStride };
                    sellersAnchor = await OpenItemOrderWindowAsync(rowAnchor, "currentSellers.png", 0.72m, cancellationToken).ConfigureAwait(false);
                    if (sellersAnchor is null)
                    {
                        break;
                    }

                    var (titleCheck, seenTitle) = await CheckOpenedItemTitleAsync(targetItem, cancellationToken).ConfigureAwait(false);
                    if (titleCheck != ItemTitleCheck.Mismatch)
                    {
                        titleUnread = titleCheck == ItemTitleCheck.Unreadable && _textLocator is not null;
                        break;
                    }

                    wrongTitles.Add(seenTitle ?? "?");
                    _logger.LogInformation("Sell row {Row} for '{Item}' opened '{Other}'; trying the next row.", row + 1, targetItem, seenTitle);
                    await CloseItemWindowAsync(cancellationToken).ConfigureAwait(false);
                    sellersAnchor = null;
                }

                if (sellersAnchor is null)
                {
                    await CloseItemWindowAsync(cancellationToken).ConfigureAwait(false);
                    results.Add(new SellPassItemResult(
                        targetItem,
                        SellPassItemOutcome.SkippedError,
                        wrongTitles.Count > 0
                            ? $"None of the bag rows opened '{targetItem}' (saw: {string.Join(", ", wrongTitles.Select(t => $"'{t}'"))}); not listing a different item."
                            : "Order modal did not open."));
                    continue;
                }

                await SettleAsync(cancellationToken).ConfigureAwait(false);
                var typeSellPrice = _settings.TypesExactPrice && _textLocator is not null && itemPricing?.ApiLowestAskCopper is > 0;
                long? liveLowestAsk = null;
                if (typeSellPrice)
                {
                    // Before the seller-row click, which inserts our own "Selling 1/N" preview row at the top.
                    liveLowestAsk = PlausibleLowestAsk(
                        await ReadTopListPriceAsync(InDialog(SellersListRegion), SellersPriceColumnMinX, itemPricing!.ApiLowestAskCopper!.Value, cancellationToken).ConfigureAwait(false),
                        itemPricing!.ApiLowestAskCopper!.Value);
                }

                if (titleUnread && typeSellPrice && !UnreadTitleIsProven(_lastSearchShowedName, liveLowestAsk, itemPricing!.ApiLowestAskCopper!.Value))
                {
                    await CloseItemWindowAsync(cancellationToken).ConfigureAwait(false);
                    results.Add(new SellPassItemResult(
                        targetItem,
                        SellPassItemOutcome.SkippedError,
                        "Listing aborted: the item window's title could not be read and the search list or live price does not prove it; it may be a different item."));
                    continue;
                }

                // Clicking a Current Sellers row switches the dialog from "Sell Instantly" (the default, into the top buy
                // order) to "List This Item".
                await _input.ClickAsync(CenterX(sellersAnchor), CenterY(sellersAnchor) + 48, cancellationToken).ConfigureAwait(false);
                await SettleShortAsync(cancellationToken).ConfigureAwait(false);

                var maxSell = await TryFindOnceAsync("choose_max_quantity_sell.png", CaptureRegion.FullScreen1024x768, 0.80m, cancellationToken).ConfigureAwait(false)
                              ?? await TryFindOnceAsync("choose_max_quantity.png", CaptureRegion.FullScreen1024x768, 0.80m, cancellationToken).ConfigureAwait(false);
                if (maxSell is not null)
                {
                    await ClickCenterAsync(maxSell, cancellationToken).ConfigureAwait(false);
                }

                var undercutSteps = PickStepCount(undercutMinCopper, undercutMaxCopper);
                string? typedSellNote = null;
                long? typedSellCopper = null;
                if (typeSellPrice)
                {
                    var choice = ChooseTypedSellPrice(itemPricing!.ApiLowestAskCopper!.Value, liveLowestAsk, undercutSteps, itemPricing.BreakEvenCopper, itemPricing.OwnLowestListingCopper);
                    _logger.LogInformation("Typed sell price for {Item}: {Message}", targetItem, choice.Message);
                    if (choice.Target is not { } sellTarget)
                    {
                        await CloseItemWindowAsync(cancellationToken).ConfigureAwait(false);
                        results.Add(new SellPassItemResult(targetItem, SellPassItemOutcome.Held, choice.Message));
                        continue;
                    }

                    var (confirmed, confirmMessage) = await TypeAndConfirmPriceAsync(sellTarget, cancellationToken).ConfigureAwait(false);
                    _logger.LogInformation("Sell price check for {Item}: {Message}", targetItem, confirmMessage);
                    if (!confirmed)
                    {
                        await CloseItemWindowAsync(cancellationToken).ConfigureAwait(false);
                        results.Add(new SellPassItemResult(targetItem, SellPassItemOutcome.SkippedError, $"Listing aborted: {confirmMessage}"));
                        continue;
                    }

                    typedSellCopper = sellTarget;
                    typedSellNote = $" at {MarketScanItem.FormatCoins(sellTarget)} (typed)";
                }
                else
                {
                    var appliedSteps = 0;
                    for (var u = 0; u < undercutSteps; u++)
                    {
                        var downArrow = await FindPriceSpinnerArrowAsync("arrow_down.png", cancellationToken).ConfigureAwait(false);
                        if (downArrow is null) break;
                        await ClickCenterAsync(downArrow, cancellationToken).ConfigureAwait(false);
                        appliedSteps++;
                    }

                    if (appliedSteps > 0 &&
                        await TryFindOnceAsync("minimum_unit_price_reached.png", CaptureRegion.FullScreen1024x768, 0.78m, cancellationToken).ConfigureAwait(false) is not null)
                    {
                        for (var r = 0; r < appliedSteps; r++)
                        {
                            var upArrow = await FindPriceSpinnerArrowAsync("arrow_up.png", cancellationToken).ConfigureAwait(false);
                            if (upArrow is null) break;
                            await ClickCenterAsync(upArrow, cancellationToken).ConfigureAwait(false);
                        }
                    }
                }

                if (IsPaused)
                {
                    await CloseItemWindowAsync(cancellationToken).ConfigureAwait(false);
                    results.Add(new SellPassItemResult(targetItem, SellPassItemOutcome.SkippedError, "Paused before listing."));
                    break;
                }

                // Never click sell_instantly.png — always place a sell order via List This Item.
                var listButton = await WaitForTemplateAsync("ListThisItem.png", CaptureRegion.FullScreen1024x768, 0.70m, _uiWaitMs, cancellationToken).ConfigureAwait(false);
                if (listButton is not null && !IsPaused)
                {
                    await ClickCenterAndSettleAsync(listButton, cancellationToken).ConfigureAwait(false);
                    results.Add(new SellPassItemResult(targetItem, SellPassItemOutcome.Listed, $"Sell listing placed{typedSellNote}.", typedSellCopper));
                }
                else
                {
                    await SaveDiagnosticFrameAsync("list-button-missing", cancellationToken).ConfigureAwait(false);
                    results.Add(new SellPassItemResult(targetItem, SellPassItemOutcome.SkippedError, "List This Item button not detected."));
                }

                await CloseItemWindowAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await CloseItemWindowAsync(cancellationToken).ConfigureAwait(false);
            await ResetFiltersAsync(cancellationToken).ConfigureAwait(false);
            _ = await SelectBuyTabAsync(cancellationToken).ConfigureAwait(false);
        }

        return results;
    }
}
