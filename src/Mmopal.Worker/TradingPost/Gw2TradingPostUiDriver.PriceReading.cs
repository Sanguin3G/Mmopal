using Microsoft.Extensions.Logging;
using Mmopal.Contracts;
using Mmopal.Gw2;
using Mmopal.Native;
using Mmopal.Worker.Vision;

namespace Mmopal.Worker;

/// <summary>Reading prices: list rows, the dialog Total and Max. Price boxes (digit shapes, then OCR), typing and confirming a price.</summary>
public sealed partial class Gw2TradingPostUiDriver
{
    /// <summary>
    /// Live top bid from the Current Buyers list, polled briefly while the order book loads. Row prices omit zero
    /// units ("69. 95." is 69s 95c, "2,721.69." is 2,721g 69s), so each reading is resolved against the API hint.
    /// </summary>
    private Task<long?> ReadTopBuyerPriceAsync(long hintCopper, CancellationToken cancellationToken) =>
        ReadTopListPriceAsync(InDialog(BuyersListRegion), BuyersPriceColumnMinX, hintCopper, cancellationToken);

    private async Task<long?> ReadTopListPriceAsync(CaptureRegion listRegion, int priceColumnMinX, long hintCopper, CancellationToken cancellationToken)
    {
        var deadline = _utcNow() + TimeSpan.FromMilliseconds(Math.Max(1_500, Math.Min(_uiWaitMs, 4_000)));
        _topRowByShape = null;
        // The top row and nothing below it: with that row unreadable, the next one used to pass as the best price (24 Nov:
        // a listing went up at 1g 84s 98c, the lowest ask being 1g 58s 85c). Unreadable means the API price is used.
        var wholeList = listRegion;
        listRegion = listRegion with { Height = Math.Min(listRegion.Height, TopRowHeight) };
        var topRowEnd = listRegion.Y + listRegion.Height;
        while (true)
        {
            try
            {
                var frame = await _screenshots.CaptureAsync(CaptureRegion.FullScreen1024x768, cancellationToken).ConfigureAwait(false);
                // Digit shapes first: the price column only (the "Ordered"/"Available" text is left of it).
                var priceColumn = new CaptureRegion(priceColumnMinX + 8, listRegion.Y, listRegion.X + listRegion.Width - priceColumnMinX - 8, listRegion.Height);
                if (frame.Pixels.Length > 0 && CoinPriceReader.ReadFirst(frame, priceColumn) is { } glyphPrice)
                {
                    _topRowByShape = glyphPrice.Copper;
                    // Tighter than the OCR window: a shape read goes straight into the typed price.
                    if (Math.Abs(glyphPrice.Copper - hintCopper) <= hintCopper * 0.15)
                    {
                        CountReading("price", "shapes");
                        return glyphPrice.Copper;
                    }
                }

                if (frame.Pixels.Length > 0 && _textLocator is not null)
                {
                    // The whole list reads better than the strip of one row, which lost "1g 44s" of "1g 44s 81c" (24 Nov); the
                    // rows below are dropped by position.
                    foreach (var region in new[] { wholeList, listRegion })
                    {
                        var variants = new List<IReadOnlyList<OcrTextBox>>
                        {
                            await _textLocator.LocateLinesAsync(frame, region, cancellationToken).ConfigureAwait(false)
                        };
                        if (FrameVision.EnhanceTextContrast(frame, region) is { } enhanced)
                        {
                            var lines = await _textLocator.LocateLinesAsync(enhanced, enhanced.Region, cancellationToken).ConfigureAwait(false);
                            variants.Add(lines
                                .Select(line => line with { X = region.X + line.X / 2, Y = region.Y + line.Y / 2, Height = line.Height / 2 })
                                .ToArray());
                        }

                        foreach (var lines in variants)
                        {
                            if (TopRowPrice(lines, priceColumnMinX, topRowEnd, hintCopper) is { } top)
                            {
                                CountReading("price", "OCR");
                                return top;
                            }
                        }
                    }
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogDebug(exception, "Could not read the top buyer price.");
            }

            if (_utcNow() >= deadline)
            {
                CountReading("price", "missed");
                return null;
            }

            await Task.Delay(Math.Max(_pollMs, 300), cancellationToken).ConfigureAwait(false);
        }
    }

    // The top row's price as the digit shapes read it, however far from the API's.
    private long? _topRowByShape;

    /// <summary>
    /// Why the open window must be another item's: its top row clearly reads a price far from the API's (over double or
    /// under half). Falling back to the API price there would order, or worse list, a 22g item at 80s.
    /// </summary>
    private string? WrongItemPrice(string itemName, long apiCopper) =>
        _topRowByShape is { } shown && IsAnotherItemsPrice(shown, apiCopper)
            ? $"the window for '{itemName}' shows {MarketScanItem.FormatCoins(shown)} where the API says {MarketScanItem.FormatCoins(apiCopper)}; it may be a different item."
            : null;

    internal static bool IsAnotherItemsPrice(long shownCopper, long apiCopper) =>
        shownCopper > 0 && apiCopper > 0 && (shownCopper > apiCopper * 2 || shownCopper * 2 < apiCopper);

    /// <summary>The price in the list's top row: its pieces joined left to right, else one piece on its own.</summary>
    internal static long? TopRowPrice(IReadOnlyList<OcrTextBox> lines, int priceColumnMinX, int topRowEnd, long hintCopper)
    {
        var row = lines
            .Where(line => line.X >= priceColumnMinX && line.Y + line.Height / 2 < topRowEnd)
            .OrderBy(line => line.X)
            .Select(line => line.Text)
            .ToArray();
        return ParseBuyerRowPrice(string.Join(" ", row), hintCopper)
               ?? row.Select(text => ParseBuyerRowPrice(text, hintCopper)).FirstOrDefault(price => price is not null);
    }

    /// <summary>
    /// Parses a Current Buyers price cell. Zero leading units are omitted and the coin icons read as dots, so two
    /// numbers can be gold+silver or silver+copper; the interpretation closest to the API hint wins, and only from 85%
    /// to double of it (anything else is a misread or a different row). With fewer numbers than the API's price has, some
    /// were lost unless it is that very price: the "81" of 1g 44s 81c passed as 81s, the "5" of 5g 99s 93c as 5g, and a
    /// listing went up 1g too low (24 Nov).
    /// </summary>
    internal static long? ParseBuyerRowPrice(string? text, long hintCopper)
    {
        var groups = PriceDigitGroups(text);
        if (groups.Count is 0 or > 3 || hintCopper <= 0)
        {
            return null;
        }

        if (ParseGroupValues(groups) is not { } values)
        {
            return null;
        }

        var candidates = new List<long>();
        void Add(long gold, long silver, long copper)
        {
            if (silver is >= 0 and <= 99 && copper is >= 0 and <= 99 && gold >= 0)
            {
                candidates.Add(gold * 10_000 + silver * 100 + copper);
            }
        }

        switch (values.Count)
        {
            case 3:
                Add(values[0], values[1], values[2]);
                break;
            case 2:
                Add(values[0], values[1], 0);
                Add(0, values[0], values[1]);
                break;
            default:
                Add(values[0], 0, 0);
                Add(0, values[0], 0);
                Add(0, 0, values[0]);
                break;
        }

        var best = candidates
            .Where(price => price > 0)
            .OrderBy(price => Math.Abs(Math.Log((double)price / hintCopper)))
            .Cast<long?>()
            .FirstOrDefault();
        var numbersExpected = hintCopper >= 10_000 ? 3 : hintCopper >= 100 ? 2 : 1;
        return best is { } chosen &&
               chosen * 100 >= hintCopper * 85 && chosen <= hintCopper * 2 &&
               (values.Count >= numbersExpected || chosen == hintCopper)
            ? chosen
            : null;
    }

    /// <summary>
    /// Types the unit price and confirms it on the plain Total line (a whole multiple of the unit price, since the
    /// quantity may be more than 1, but never above <paramref name="maxQuantity"/>: on 20 Nov a "2" typed into a
    /// quantity box that still held its "1" ordered 21). A box that missed its click shows up as a different total;
    /// that is re-typed once before giving up. A total that stays unreadable is not confirmed either: on 22 Nov one
    /// such order went in at 95g instead of the typed 21g.
    /// </summary>
    private async Task<(bool Confirmed, string Message)> TypeAndConfirmPriceAsync(long unitCopper, CancellationToken cancellationToken, long maxQuantity = 250)
    {
        long? lastSeen = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            if (!await TypeDialogPriceAsync(unitCopper, cancellationToken).ConfigureAwait(false))
            {
                await SaveDiagnosticFrameAsync("price-boxes-missing", cancellationToken).ConfigureAwait(false);
                return (false, "the price boxes are not on screen, so nothing was typed.");
            }

            // Digit shapes first: exact, and immune to the OCR failures below. OCR stays as the fallback.
            var glyphs = await ReadTotalByGlyphsAsync(cancellationToken).ConfigureAwait(false);
            if (glyphs.Reading is { } early && !IsWholeMultiple(early.Copper, unitCopper, maxQuantity))
            {
                // The Total follows the boxes late when the game lags (13 times on 25 Nov): look again before re-typing.
                await Task.Delay(_requireTemplateFilesOnDisk ? 800 : 0, cancellationToken).ConfigureAwait(false);
                glyphs = await ReadTotalByGlyphsAsync(cancellationToken).ConfigureAwait(false);
            }

            if (glyphs.Reading is { } glyphTotal && IsWholeMultiple(glyphTotal.Copper, unitCopper, maxQuantity))
            {
                CountReading("total", "shapes");
                return (true, $"Typed {MarketScanItem.FormatCoins(unitCopper)} confirmed (read by digit shapes: {glyphTotal.Text})" +
                              (attempt == 1 ? "." : $" after re-typing (first try showed {MarketScanItem.FormatCoins(lastSeen ?? 0)})."));
            }

            var texts = await ReadJoinedTextVariantsAsync(InDialog(ItemDialogTotalPriceRegion), cancellationToken).ConfigureAwait(false);
            var totals = texts
                .Select(ParseTotalPrice)
                .Where(total => total is > 0)
                .Select(total => total!.Value)
                .ToArray();
            if (totals.Length == 0 && glyphs.Reading is null)
            {
                if (attempt == 1)
                {
                    await SettleAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                CountReading("total", "missed");
                await SaveDiagnosticFrameAsync("total-unreadable", cancellationToken).ConfigureAwait(false);
                return (false, $"typed {MarketScanItem.FormatCoins(unitCopper)} twice but the dialog total could not be read to confirm it.");
            }

            if (totals.FirstOrDefault(total => IsWholeMultiple(total, unitCopper, maxQuantity)) is var ocrTotal and > 0)
            {
                // OCR confirmed what the digit shapes could not read: learn the unknown shapes for next time.
                // Only when two OCR reads agree on it: a learned shape is kept for good, so its label must be right.
                if (glyphs.Frame is { } frame && glyphs.Reading is null && totals.Count(total => total == ocrTotal) >= 2 &&
                    CoinPriceReader.LearnFromConfirmed(frame, InDialog(GlyphTotalRegion), ocrTotal) is var learned and > 0)
                {
                    _logger.LogInformation("Learned {Count} new digit shape(s) from the confirmed total {Total}.", learned, MarketScanItem.FormatCoins(ocrTotal));
                }

                CountReading("total", "OCR");
                return (true, attempt == 1
                    ? $"Typed {MarketScanItem.FormatCoins(unitCopper)} confirmed."
                    : $"Typed {MarketScanItem.FormatCoins(unitCopper)} confirmed after re-typing (first try showed {MarketScanItem.FormatCoins(lastSeen ?? 0)}).");
            }

            if (glyphs.Reading is { } shown && !TotalMatchesAcrossReads(texts, unitCopper, maxQuantity))
            {
                // The shapes read a definite, different total and OCR does not confirm the typed one either: the boxes
                // did not take the value. Re-type, never assume.
                lastSeen = shown.Copper;
                if (attempt == 1)
                {
                    _logger.LogInformation(
                        "Dialog shows {Seen} after typing {Typed} (read by digit shapes: {Text}); re-typing once.",
                        MarketScanItem.FormatCoins(shown.Copper),
                        MarketScanItem.FormatCoins(unitCopper),
                        shown.Text);
                }

                continue;
            }

            // OCR often loses a "00"/"06" group on this line ("1g 00s 18c" read as "1 18"), which shifted the parse by
            // one coin and aborted 43 good orders in one 7 h session. Accept a read that is the expected total with only
            // zero-containing groups missing; a box that really missed its value still shows different digits.
            // The raw and enhanced reads also fail differently ("56 56" and "2 56" for 2g 56s 56c; "81" and "88€ 814",
            // the coin icon read as a 4, for 88s 81c), so the reads are combined: each must fit the expected total and
            // together they must show every digit group that is not zero-ish (and always the gold).
            if (TotalMatchesAcrossReads(texts, unitCopper, maxQuantity))
            {
                CountReading("total", "OCR");
                return (true, $"Typed {MarketScanItem.FormatCoins(unitCopper)} confirmed (total read as '{JoinReads(texts)}').");
            }

            lastSeen = totals[0];
            if (attempt == 1)
            {
                _logger.LogInformation(
                    "Dialog shows {Seen} after typing {Typed} (read: '{Raw}'); re-typing once.",
                    MarketScanItem.FormatCoins(lastSeen.Value),
                    MarketScanItem.FormatCoins(unitCopper),
                    JoinReads(texts));
            }
        }

        await SaveDiagnosticFrameAsync("total-mismatch", cancellationToken).ConfigureAwait(false);
        return (false, $"typed {MarketScanItem.FormatCoins(unitCopper)} twice but the dialog total shows {MarketScanItem.FormatCoins(lastSeen ?? 0)}; the price boxes did not take the typed value.");

        static string JoinReads(IReadOnlyList<string> reads) => string.Join(" | ", reads.Select(read => read.Trim()));
    }

    private async Task<(CoinReading? Reading, AutomationFrame? Frame)> ReadTotalByGlyphsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var frame = await _screenshots.CaptureAsync(CaptureRegion.FullScreen1024x768, cancellationToken).ConfigureAwait(false);
            return frame.Pixels.Length == 0 ? (null, null) : (CoinPriceReader.ReadFirst(frame, InDialog(GlyphTotalRegion)), frame);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogDebug(exception, "Digit-shape read of the dialog total failed.");
            return (null, null);
        }
    }

    /// <summary>
    /// True when the Total line's digit groups equal unit × q (q = 1..250) with only "zero-ish" groups missing: a
    /// silver or copper value whose two-digit form contains a 0 (00, 06, 50 …), which OCR tends to drop. Gold is never
    /// allowed to be missing, and no group may be extra or different.
    /// </summary>
    internal static bool TotalMatchesWithUnreadZeroGroups(string? text, long unitCopper) =>
        TotalMatchesAcrossReads([text ?? string.Empty], unitCopper);

    /// <summary>
    /// True when the Total line reads fit unit × q (q = 1..250): every read that parses must be the expected digit
    /// groups in order with some missing, and the reads together must show the gold group and every silver/copper group
    /// whose two-digit form has no 0 (OCR drops "00", "06", "50" …). A 3-digit read in a silver/copper place may carry the
    /// coin icon as a last digit ("814" for 81). A box that missed its value shows other digits and never matches.
    /// </summary>
    internal static bool TotalMatchesAcrossReads(IReadOnlyList<string> texts, long unitCopper, long maxQuantity = 250)
    {
        if (unitCopper <= 0)
        {
            return false;
        }

        var reads = new List<List<long>>();
        foreach (var text in texts)
        {
            if (ParseGroupValues(PriceDigitGroups(text)) is { Count: > 0 } groups) reads.Add(groups);
        }

        if (reads.Count == 0)
        {
            return false;
        }

        for (var quantity = 1; quantity <= Math.Clamp(maxQuantity, 1, 250); quantity++)
        {
            var total = unitCopper * quantity;
            var (gold, silver, copper) = (total / 10_000, total / 100 % 100, total % 100);
            var expected = new List<(long Value, bool MustBeSeen, bool IsCoinPlace)>();
            if (gold > 0) expected.Add((gold, true, false));
            if (gold > 0 || silver > 0) expected.Add((silver, !IsZeroish(silver), true));
            expected.Add((copper, !IsZeroish(copper), true));

            var seen = new bool[expected.Count];
            var anyFits = false;
            foreach (var read in reads)
            {
                var positions = AlignRead(read, expected);
                if (positions is null) continue;
                anyFits = true;
                foreach (var position in positions) seen[position] = true;
            }

            if (anyFits && expected.Select((e, i) => !e.MustBeSeen || seen[i]).All(ok => ok))
            {
                return true;
            }
        }

        return false;

        static bool IsZeroish(long twoDigit) => twoDigit < 10 || twoDigit % 10 == 0;
    }

    /// <summary>Positions of <paramref name="expected"/> that <paramref name="read"/> matches in order, or null.</summary>
    private static List<int>? AlignRead(List<long> read, List<(long Value, bool MustBeSeen, bool IsCoinPlace)> expected)
    {
        var positions = new List<int>(read.Count);
        var e = 0;
        foreach (var value in read)
        {
            while (e < expected.Count &&
                   value != expected[e].Value &&
                   !(expected[e].IsCoinPlace && value is >= 100 and <= 999 && value / 10 == expected[e].Value))
            {
                e++;
            }

            if (e >= expected.Count) return null;
            positions.Add(e);
            e++;
        }

        return positions;
    }

    /// <summary>
    /// Typed sell price: the live lowest ask (else the API's) minus the undercut, but never below break-even (what
    /// Mmopal paid, covered after the 15% fees). If undercutting would go below break-even, the item is held.
    /// </summary>
    internal static (long? Target, string Message) ChooseTypedSellPrice(
        long apiLowestAskCopper,
        long? liveLowestAskCopper,
        int undercutSteps,
        long? breakEvenCopper,
        long? ownLowestListingCopper = null)
    {
        var basis = liveLowestAskCopper ?? apiLowestAskCopper;
        // The lowest ask is already our own listing: join it at the same price. Undercutting it only lowered our own
        // price each time (22 Nov: Magi's Intricate Gossamer Insignia listed at 66s 30c, 28c, 26c, 24c and 22c).
        var joinsOwnListing = ownLowestListingCopper is { } own && own == basis;
        if (joinsOwnListing)
        {
            undercutSteps = 0;
        }

        var target = Math.Max(1, basis - Math.Max(0, undercutSteps));
        var source = liveLowestAskCopper is null
            ? $"API lowest ask {MarketScanItem.FormatCoins(apiLowestAskCopper)} (live lowest ask unreadable)"
            : $"live lowest ask {MarketScanItem.FormatCoins(basis)} (API {MarketScanItem.FormatCoins(apiLowestAskCopper)})";
        if (breakEvenCopper is { } floor && target < floor)
        {
            return (null, $"holding: {source} - {undercutSteps}c = {MarketScanItem.FormatCoins(target)} is below break-even {MarketScanItem.FormatCoins(floor)}; checking again in 30 min.");
        }

        if (joinsOwnListing)
        {
            return (target, $"{source} is our own listing -> typing the same {MarketScanItem.FormatCoins(target)} instead of undercutting it"
                            + (breakEvenCopper is { } ownBe ? $" (break-even {MarketScanItem.FormatCoins(ownBe)})." : "."));
        }

        return (target, $"{source} - {undercutSteps}c -> typing {MarketScanItem.FormatCoins(target)}"
                        + (breakEvenCopper is { } be ? $" (break-even {MarketScanItem.FormatCoins(be)})." : "."));
    }

    /// <summary>
    /// The lowest ask to work from. A live read clearly above the API's is another row or a stale list: the API's lower
    /// price sells, and break-even still guards it.
    /// </summary>
    internal static long? PlausibleLowestAsk(long? liveCopper, long apiCopper) =>
        liveCopper is { } live && apiCopper > 0 && live > apiCopper * 1.06 ? null : liveCopper;

    internal static bool IsWholeMultiple(long total, long unit, long maxQuantity = 250) =>
        unit > 0 && total % unit == 0 && total / unit >= 1 && total / unit <= maxQuantity;

    /// <summary>
    /// Clicks each Max. Price box (gold, silver, copper), selects its contents and types the value. Box positions are
    /// taken from the copper ▲ spinner (measured at (601, 304) on live frames); false without it, since the same clicks
    /// and keys would land in the game.
    /// </summary>
    private async Task<bool> TypeDialogPriceAsync(long unitCopper, CancellationToken cancellationToken)
    {
        var gold = unitCopper / 10_000;
        var silver = unitCopper / 100 % 100;
        var copper = unitCopper % 100;
        if (await WaitForPriceSpinnerArrowAsync("arrow_up.png", cancellationToken).ConfigureAwait(false) is not { } arrow)
        {
            return false;
        }

        var (anchorX, anchorY) = (CenterX(arrow), CenterY(arrow));
        var boxes = new (int X, int Value)[]
        {
            (anchorX - 176, (int)Math.Min(gold, int.MaxValue)),
            (anchorX - 93, (int)silver),
            (anchorX - 21, (int)copper)
        };
        foreach (var (x, value) in boxes)
        {
            if (!await TradingPostProvenAsync(cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            await _input.ClickAsync(x, anchorY + 7, cancellationToken).ConfigureAwait(false);
            await SettleShortAsync(cancellationToken).ConfigureAwait(false);
            await ReplaceFieldTextAsync(value.ToString(System.Globalization.CultureInfo.InvariantCulture), LongestNumberText, cancellationToken).ConfigureAwait(false);
            await SettleShortAsync(cancellationToken).ConfigureAwait(false);
        }

        // The Total updates as digits are typed; give the dialog a moment before it is read.
        await SettleAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>Reads the buy dialog's Max. Price (per unit) in copper, or null when it cannot be read.</summary>
    /// <remarks>
    /// Reads the editable Max. Price boxes first; live, these often failed to OCR right after the ▲ clicks (7 of 124
    /// reads in one session), so it retries once and then falls back to the plain "Total Price" line divided by the
    /// quantity, accepted only when it divides exactly.
    /// </remarks>
    private async Task<(long? Unit, bool Confident)> ReadDialogUnitPriceAsync(long quantity, CancellationToken cancellationToken)
    {
        long? fromBoxes = null;
        for (var pass = 0; pass < 2; pass++)
        {
            if (pass > 0)
            {
                await Task.Delay(Math.Max(150, _settleMs / 3), cancellationToken).ConfigureAwait(false);
            }

            foreach (var text in await ReadJoinedTextVariantsAsync(InDialog(ItemDialogTotalPriceRegion), cancellationToken).ConfigureAwait(false))
            {
                if (ParseTotalPrice(text) is { } total && quantity > 0 && total % quantity == 0)
                {
                    return (total / quantity, true);
                }
            }

            foreach (var text in await ReadJoinedTextVariantsAsync(InDialog(ItemDialogMaxPriceRegion), cancellationToken).ConfigureAwait(false))
            {
                fromBoxes ??= ParseDialogPrice(text);
            }
        }

        return (fromBoxes, false);
    }

    /// <summary>
    /// The region's OCR as one string per variant (raw, enhanced), lines joined left to right; the enhanced pass can
    /// split "3,469. 69." and "67." into separate lines.
    /// </summary>
    private async Task<IReadOnlyList<string>> ReadJoinedTextVariantsAsync(CaptureRegion region, CancellationToken cancellationToken)
    {
        var variants = await ReadOcrLineVariantsAsync(region, cancellationToken).ConfigureAwait(false);
        return variants
            .Select(lines => string.Join(" ", lines.OrderBy(line => line.X).Select(line => line.Text)))
            .ToArray();
    }

    /// <summary>OCR lines of a region, first from the raw frame and then from a contrast-enhanced 2x copy.</summary>
    private async Task<IReadOnlyList<string>> ReadTextVariantsAsync(CaptureRegion region, CancellationToken cancellationToken, bool enlargeMore = false)
    {
        var variants = await ReadOcrLineVariantsAsync(region, cancellationToken, enlargeMore).ConfigureAwait(false);
        return variants
            .SelectMany(lines => lines.OrderBy(line => line.Y).Select(line => line.Text))
            .ToArray();
    }

    /// <summary>
    /// The region's OCR lines from the raw frame, then from the contrast-enhanced copy when one can be built. The
    /// enhanced lines keep the enhanced frame's coordinates; callers only use them for ordering.
    /// </summary>
    private async Task<IReadOnlyList<IReadOnlyList<OcrTextBox>>> ReadOcrLineVariantsAsync(CaptureRegion region, CancellationToken cancellationToken, bool enlargeMore = false)
    {
        if (_textLocator is null)
        {
            return [];
        }

        try
        {
            var frame = await _screenshots.CaptureAsync(CaptureRegion.FullScreen1024x768, cancellationToken).ConfigureAwait(false);
            if (frame.Pixels.Length == 0)
            {
                return [];
            }

            var variants = new List<IReadOnlyList<OcrTextBox>>(2)
            {
                await _textLocator.LocateLinesAsync(frame, region, cancellationToken).ConfigureAwait(false)
            };
            if (FrameVision.EnhanceTextContrast(frame, region) is { } enhanced)
            {
                variants.Add(await _textLocator.LocateLinesAsync(enhanced, enhanced.Region, cancellationToken).ConfigureAwait(false));
            }

            if (enlargeMore && FrameVision.EnhanceTextContrast(frame, region, 3) is { } larger)
            {
                variants.Add(await _textLocator.LocateLinesAsync(larger, larger.Region, cancellationToken).ConfigureAwait(false));
            }

            return variants;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogDebug(exception, "OCR of region {Region} failed.", region);
            return [];
        }
    }

    /// <summary>
    /// Parses the Max. Price gold/silver/copper boxes, e.g. "3469: 69: • 67:" → 34,696,967c. Live OCR reads gold 0
    /// as "o", 1 as "I", and often reads the ▴▾ spinner after a box as an extra "2" ("o: 582 e 99:" for 0 · 58 · 99):
    /// a 3-digit silver or copper value loses its last digit. Gold cannot be corrected that way, so callers treat this reading as unconfirmed.
    /// </summary>
    internal static long? ParseDialogPrice(string? text)
    {
        var groups = PriceDigitGroups(text);
        if (groups.Count < 3 || !long.TryParse(groups[0], out var gold) || gold is < 0 or > 100_000)
        {
            return null;
        }

        var silver = SpinnerCorrected(groups[1]);
        var copper = SpinnerCorrected(groups[2]);
        return silver is null || copper is null ? null : gold * 10_000 + silver.Value * 100L + copper.Value;
    }

    /// <summary>
    /// Parses the "Total Price:" line, e.g. "Total Price: I e 17 • 98." (1g 17s 98c) or "Total Price: 47. 34 e"
    /// (47s 34c: the game omits gold when it is 0). No spinners on this line, so values are taken as read.
    /// </summary>
    internal static long? ParseTotalPrice(string? text)
    {
        var groups = PriceDigitGroups(text);
        // "11" is read as "I I": with four groups, two leading ones are one number (Zojja's Berserker Insignia, 24 Nov).
        if (groups.Count == 4 && groups[0] == "1" && groups[1] == "1")
        {
            groups = ["11", groups[2], groups[3]];
        }

        if (groups.Count is 0 or > 3)
        {
            return null;
        }

        var values = new long[3];
        for (var i = 0; i < groups.Count; i++)
        {
            if (!long.TryParse(groups[i], out values[3 - groups.Count + i]))
            {
                return null;
            }
        }

        var (gold, silver, copper) = (values[0], values[1], values[2]);
        return silver > 99 || copper > 99 || gold > 1_000_000 ? null : gold * 10_000 + silver * 100 + copper;
    }

    private static int? SpinnerCorrected(string digits)
    {
        if (!int.TryParse(digits, out var value) || value < 0)
        {
            return null;
        }

        if (value <= 99)
        {
            return value;
        }

        // A 3-digit reading is the value plus the spinner read as a trailing digit.
        return value <= 999 ? value / 10 : null;
    }

    /// <summary>The digit groups as numbers, or null when any group does not parse.</summary>
    private static List<long>? ParseGroupValues(List<string> groups)
    {
        var values = new List<long>(groups.Count);
        foreach (var group in groups)
        {
            if (!long.TryParse(group, out var value)) return null;
            values.Add(value);
        }

        return values;
    }

    /// <summary>Digit groups after the "Price" label, with OCR letter look-alikes mapped (o/O → 0, I/l/| → 1).</summary>
    private static List<string> PriceDigitGroups(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var labelEnd = text.LastIndexOf("rice", StringComparison.OrdinalIgnoreCase);
        var values = labelEnd >= 0 ? text[(labelEnd + 4)..] : text;
        var mapped = new string(values.Select(c => c switch
        {
            'o' or 'O' => '0',
            'I' or 'l' or '|' => '1',
            _ => c
        }).ToArray());
        return System.Text.RegularExpressions.Regex.Matches(mapped, @"\d[\d,]*")
            .Select(match => match.Value.Replace(",", string.Empty, StringComparison.Ordinal))
            .Where(value => value.Length > 0)
            .ToList();
    }
}
