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
        ReadTopListPriceAsync(BuyersListRegion, BuyersPriceColumnMinX, hintCopper, cancellationToken);

    private async Task<long?> ReadTopListPriceAsync(CaptureRegion listRegion, int priceColumnMinX, long hintCopper, CancellationToken cancellationToken)
    {
        var deadline = _utcNow() + TimeSpan.FromMilliseconds(Math.Max(1_500, Math.Min(_uiWaitMs, 4_000)));
        while (true)
        {
            try
            {
                var frame = await _screenshots.CaptureAsync(CaptureRegion.FullScreen1024x768, cancellationToken).ConfigureAwait(false);
                // Digit shapes first: the price column only (the "Ordered"/"Available" text is left of it).
                var priceColumn = new CaptureRegion(priceColumnMinX + 8, listRegion.Y, listRegion.X + listRegion.Width - priceColumnMinX - 8, listRegion.Height);
                if (frame.Pixels.Length > 0 &&
                    CoinPriceReader.ReadFirst(frame, priceColumn) is { } glyphPrice &&
                    // Tighter than the OCR window: a shape read goes straight into the typed price.
                    Math.Abs(glyphPrice.Copper - hintCopper) <= hintCopper * 0.15)
                {
                    return glyphPrice.Copper;
                }

                if (frame.Pixels.Length > 0 && _textLocator is not null)
                {
                    var variants = new List<IReadOnlyList<OcrTextBox>>
                    {
                        await _textLocator.LocateLinesAsync(frame, listRegion, cancellationToken).ConfigureAwait(false)
                    };
                    if (FrameVision.EnhanceTextContrast(frame, listRegion) is { } enhanced)
                    {
                        var lines = await _textLocator.LocateLinesAsync(enhanced, enhanced.Region, cancellationToken).ConfigureAwait(false);
                        variants.Add(lines
                            .Select(line => line with { X = listRegion.X + line.X / 2, Y = listRegion.Y + line.Y / 2 })
                            .ToArray());
                    }

                    foreach (var lines in variants)
                    {
                        var top = lines
                            .Where(line => line.X >= priceColumnMinX)
                            .OrderBy(line => line.Y)
                            .Select(line => ParseBuyerRowPrice(line.Text, hintCopper))
                            .FirstOrDefault(price => price is not null);
                        if (top is not null)
                        {
                            return top;
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
                return null;
            }

            await Task.Delay(Math.Max(_pollMs, 300), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Parses a Current Buyers price cell. Zero leading units are omitted and the coin icons read as dots, so two
    /// numbers can be gold+silver or silver+copper; the interpretation closest to the API hint wins, and only within
    /// half to double of it (anything else is a misread or a different row).
    /// </summary>
    internal static long? ParseBuyerRowPrice(string? text, long hintCopper)
    {
        var groups = PriceDigitGroups(text);
        if (groups.Count is 0 or > 3 || hintCopper <= 0)
        {
            return null;
        }

        var values = new List<long>();
        foreach (var group in groups)
        {
            if (!long.TryParse(group, out var value)) return null;
            values.Add(value);
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
        return best is { } chosen && chosen * 2 >= hintCopper && chosen <= hintCopper * 2 ? chosen : null;
    }

    /// <summary>
    /// Types the unit price and confirms it on the plain Total line (a whole multiple of the unit price, since the
    /// quantity may be more than 1). A box that missed its click shows up as a different total; that is re-typed once
    /// before giving up. An unreadable total proceeds: the typed value is deterministic and the API audit follows.
    /// </summary>
    private async Task<(bool Confirmed, string Message)> TypeAndConfirmPriceAsync(long unitCopper, CancellationToken cancellationToken)
    {
        long? lastSeen = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            await TypeDialogPriceAsync(unitCopper, cancellationToken).ConfigureAwait(false);

            // Digit shapes first: exact, and immune to the OCR failures below. OCR stays as the fallback.
            var glyphs = await ReadTotalByGlyphsAsync(cancellationToken).ConfigureAwait(false);
            if (glyphs.Reading is { } glyphTotal && IsWholeMultiple(glyphTotal.Copper, unitCopper))
            {
                return (true, $"Typed {MarketScanItem.FormatCoins(unitCopper)} confirmed (read by digit shapes: {glyphTotal.Text})" +
                              (attempt == 1 ? "." : $" after re-typing (first try showed {MarketScanItem.FormatCoins(lastSeen ?? 0)})."));
            }

            var texts = await ReadJoinedTextVariantsAsync(ItemDialogTotalPriceRegion, cancellationToken).ConfigureAwait(false);
            var totals = texts
                .Select(ParseTotalPrice)
                .Where(total => total is > 0)
                .Select(total => total!.Value)
                .ToArray();
            if (totals.Length == 0 && glyphs.Reading is null)
            {
                return (true, $"Typed {MarketScanItem.FormatCoins(unitCopper)}; the dialog total could not be read to confirm it.");
            }

            if (totals.FirstOrDefault(total => IsWholeMultiple(total, unitCopper)) is var ocrTotal and > 0)
            {
                // OCR confirmed what the digit shapes could not read: learn the unknown shapes for next time.
                // Only when two OCR reads agree on it: a learned shape is kept for good, so its label must be right.
                if (glyphs.Frame is { } frame && glyphs.Reading is null && totals.Count(total => total == ocrTotal) >= 2 &&
                    CoinPriceReader.LearnFromConfirmed(frame, GlyphTotalRegion, ocrTotal) is var learned and > 0)
                {
                    _logger.LogInformation("Learned {Count} new digit shape(s) from the confirmed total {Total}.", learned, MarketScanItem.FormatCoins(ocrTotal));
                }

                return (true, attempt == 1
                    ? $"Typed {MarketScanItem.FormatCoins(unitCopper)} confirmed."
                    : $"Typed {MarketScanItem.FormatCoins(unitCopper)} confirmed after re-typing (first try showed {MarketScanItem.FormatCoins(lastSeen ?? 0)}).");
            }

            if (glyphs.Reading is { } shown && !TotalMatchesAcrossReads(texts, unitCopper))
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
            if (TotalMatchesAcrossReads(texts, unitCopper))
            {
                return (true, $"Typed {MarketScanItem.FormatCoins(unitCopper)} confirmed (total read as '{string.Join(" | ", texts.Select(text => text.Trim()))}').");
            }

            lastSeen = totals[0];
            if (attempt == 1)
            {
                _logger.LogInformation(
                    "Dialog shows {Seen} after typing {Typed} (read: '{Raw}'); re-typing once.",
                    MarketScanItem.FormatCoins(lastSeen.Value),
                    MarketScanItem.FormatCoins(unitCopper),
                    string.Join(" | ", texts.Select(text => text.Trim())));
            }
        }

        return (false, $"typed {MarketScanItem.FormatCoins(unitCopper)} twice but the dialog total shows {MarketScanItem.FormatCoins(lastSeen ?? 0)}; the price boxes did not take the typed value.");
    }

    private async Task<(CoinReading? Reading, AutomationFrame? Frame)> ReadTotalByGlyphsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var frame = await _screenshots.CaptureAsync(CaptureRegion.FullScreen1024x768, cancellationToken).ConfigureAwait(false);
            return frame.Pixels.Length == 0 ? (null, null) : (CoinPriceReader.ReadFirst(frame, GlyphTotalRegion), frame);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogDebug(exception, "Digit-shape read of the dialog total failed.");
            return (null, null);
        }
    }

    internal static bool TotalMatchesWithUnreadZeroGroups(string? text, long unitCopper) =>
        TotalMatchesAcrossReads([text ?? string.Empty], unitCopper);

    /// <summary>
    /// True when the Total line reads fit unit × q (q = 1..250): every read that parses must be the expected digit
    /// groups in order with some missing, and the reads together must show the gold group and every silver/copper group
    /// whose two-digit form has no 0 (OCR drops "00", "06", "50" …). A 3-digit read in a silver/copper place may carry the
    /// coin icon as a last digit ("814" for 81). A box that missed its value shows other digits and never matches.
    /// </summary>
    internal static bool TotalMatchesAcrossReads(IReadOnlyList<string> texts, long unitCopper)
    {
        if (unitCopper <= 0)
        {
            return false;
        }

        var reads = new List<List<long>>();
        foreach (var text in texts)
        {
            var groups = new List<long>();
            var parsed = true;
            foreach (var group in PriceDigitGroups(text))
            {
                if (!long.TryParse(group, out var value)) { parsed = false; break; }
                groups.Add(value);
            }

            if (parsed && groups.Count > 0) reads.Add(groups);
        }

        if (reads.Count == 0)
        {
            return false;
        }

        for (var quantity = 1; quantity <= 250; quantity++)
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
        long? breakEvenCopper)
    {
        var basis = liveLowestAskCopper ?? apiLowestAskCopper;
        var target = Math.Max(1, basis - Math.Max(0, undercutSteps));
        var source = liveLowestAskCopper is null
            ? $"API lowest ask {MarketScanItem.FormatCoins(apiLowestAskCopper)} (live lowest ask unreadable)"
            : $"live lowest ask {MarketScanItem.FormatCoins(basis)} (API {MarketScanItem.FormatCoins(apiLowestAskCopper)})";
        if (breakEvenCopper is { } floor && target < floor)
        {
            return (null, $"holding: {source} - {undercutSteps}c = {MarketScanItem.FormatCoins(target)} is below break-even {MarketScanItem.FormatCoins(floor)}; checking again in 30 min.");
        }

        return (target, $"{source} - {undercutSteps}c -> typing {MarketScanItem.FormatCoins(target)}"
                        + (breakEvenCopper is { } be ? $" (break-even {MarketScanItem.FormatCoins(be)})." : "."));
    }

    internal static bool IsWholeMultiple(long total, long unit) =>
        unit > 0 && total % unit == 0 && total / unit is >= 1 and <= 250;

    /// <summary>
    /// Clicks each Max. Price box (gold, silver, copper), selects its contents and types the value. Box positions are
    /// taken from the copper ▲ spinner (measured at (601, 304) on live frames), falling back to fixed coordinates.
    /// </summary>
    private async Task TypeDialogPriceAsync(long unitCopper, CancellationToken cancellationToken)
    {
        var gold = unitCopper / 10_000;
        var silver = unitCopper / 100 % 100;
        var copper = unitCopper % 100;
        var arrow = await FindPriceSpinnerArrowAsync("arrow_up.png", cancellationToken).ConfigureAwait(false);
        var (anchorX, anchorY) = arrow is null ? (601, 304) : (CenterX(arrow), CenterY(arrow));
        var boxes = new (int X, int Value)[]
        {
            (anchorX - 176, (int)Math.Min(gold, int.MaxValue)),
            (anchorX - 93, (int)silver),
            (anchorX - 21, (int)copper)
        };
        foreach (var (x, value) in boxes)
        {
            await _input.ClickAsync(x, anchorY + 7, cancellationToken).ConfigureAwait(false);
            await SettleShortAsync(cancellationToken).ConfigureAwait(false);
            await ReplaceFieldTextAsync(value.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
            await SettleShortAsync(cancellationToken).ConfigureAwait(false);
        }

        // The Total updates as digits are typed; give the dialog a moment before it is read.
        await SettleAsync(cancellationToken).ConfigureAwait(false);
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

            foreach (var text in await ReadJoinedTextVariantsAsync(ItemDialogTotalPriceRegion, cancellationToken).ConfigureAwait(false))
            {
                if (ParseTotalPrice(text) is { } total && quantity > 0 && total % quantity == 0)
                {
                    return (total / quantity, true);
                }
            }

            foreach (var text in await ReadJoinedTextVariantsAsync(ItemDialogMaxPriceRegion, cancellationToken).ConfigureAwait(false))
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
        if (_textLocator is null)
        {
            return Array.Empty<string>();
        }

        try
        {
            var frame = await _screenshots.CaptureAsync(CaptureRegion.FullScreen1024x768, cancellationToken).ConfigureAwait(false);
            if (frame.Pixels.Length == 0)
            {
                return Array.Empty<string>();
            }

            var variants = new List<string>(2);
            var raw = await _textLocator.LocateLinesAsync(frame, region, cancellationToken).ConfigureAwait(false);
            variants.Add(string.Join(" ", raw.OrderBy(line => line.X).Select(line => line.Text)));
            if (FrameVision.EnhanceTextContrast(frame, region) is { } enhanced)
            {
                var lines = await _textLocator.LocateLinesAsync(enhanced, enhanced.Region, cancellationToken).ConfigureAwait(false);
                variants.Add(string.Join(" ", lines.OrderBy(line => line.X).Select(line => line.Text)));
            }

            return variants;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogDebug(exception, "OCR of region {Region} failed.", region);
            return Array.Empty<string>();
        }
    }

    /// <summary>OCR lines of a region, first from the raw frame and then from a contrast-enhanced 2x copy.</summary>
    private async Task<IReadOnlyList<string>> ReadTextVariantsAsync(CaptureRegion region, CancellationToken cancellationToken)
    {
        if (_textLocator is null)
        {
            return Array.Empty<string>();
        }

        try
        {
            var frame = await _screenshots.CaptureAsync(CaptureRegion.FullScreen1024x768, cancellationToken).ConfigureAwait(false);
            if (frame.Pixels.Length == 0)
            {
                return Array.Empty<string>();
            }

            var texts = new List<string>();
            var raw = await _textLocator.LocateLinesAsync(frame, region, cancellationToken).ConfigureAwait(false);
            texts.AddRange(raw.OrderBy(line => line.Y).Select(line => line.Text));
            if (FrameVision.EnhanceTextContrast(frame, region) is { } enhanced)
            {
                var lines = await _textLocator.LocateLinesAsync(enhanced, enhanced.Region, cancellationToken).ConfigureAwait(false);
                texts.AddRange(lines.OrderBy(line => line.Y).Select(line => line.Text));
            }

            return texts;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogDebug(exception, "OCR of region {Region} failed.", region);
            return Array.Empty<string>();
        }
    }

    /// <summary>Parses the dialog's "gold : silver : copper" boxes, e.g. "3469: 69: • 67:" → 34,696,967c.</summary>
    /// <summary>
    /// Parses the Max. Price gold/silver/copper boxes. Live OCR reads gold 0 as "o", 1 as "I", and often reads the
    /// ▴▾ spinner after a box as an extra "2" ("o: 582 e 99:" for 0 · 58 · 99): a 3-digit silver or copper value
    /// loses its last digit. Gold cannot be corrected that way, so callers treat this reading as unconfirmed.
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

        return value <= 99 ? value : value <= 999 ? value / 10 : null;
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
