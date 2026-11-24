using Microsoft.Extensions.Logging;
using Mmopal.Contracts;
using Mmopal.Gw2;
using Mmopal.Native;
using Mmopal.Worker.Vision;

namespace Mmopal.Worker;

/// <summary>Trading Post search: typing a name, waiting for results, opening the right row, item-title checks.</summary>
public sealed partial class Gw2TradingPostUiDriver
{
    /// <summary>Reads the open buy dialog's title (raw and contrast-enhanced) and compares it with the expected item.</summary>
    private async Task<(ItemTitleCheck Result, string? SeenTitle)> CheckOpenedItemTitleAsync(string expectedName, CancellationToken cancellationToken)
    {
        if (_textLocator is null)
        {
            return (ItemTitleCheck.Unreadable, null);
        }

        var check = await ReadItemTitleAsync(expectedName, cancellationToken).ConfigureAwait(false);
        if (check.Result != ItemTitleCheck.Unreadable)
        {
            return check;
        }

        // The title may still be drawing: one more read before calling it unreadable.
        await SettleAsync(cancellationToken).ConfigureAwait(false);
        return await ReadItemTitleAsync(expectedName, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the title. A long one wraps ("Soldier's Benthic Waterbreather of the / Afflicted"): then the second line is
    /// the proof, and everything below it sits lower. On 24 Nov that item was refused four times because its price boxes
    /// were not where a one-line title leaves them, and the second seller row was read as the lowest ask.
    /// </summary>
    private async Task<(ItemTitleCheck Result, string? SeenTitle)> ReadItemTitleAsync(string expectedName, CancellationToken cancellationToken)
    {
        var texts = await ReadTextVariantsAsync(ItemDialogTitleRegion, cancellationToken).ConfigureAwait(false);
        if (texts.Any(text => IsSameItemName(expectedName, text)))
        {
            if (_openDialogAnchor is { } usual)
            {
                _usualAnchorY[usual.Template] = usual.Y;
            }

            return ClassifyItemTitle(expectedName, texts);
        }

        if (texts.Select(text => SecondTitleLine(expectedName, text)).FirstOrDefault(rest => rest is not null) is not { } secondLine)
        {
            return ClassifyItemTitle(expectedName, texts);
        }

        if (_openDialogAnchor is { } anchor)
        {
            _dialogShiftY = DialogShift(anchor.Y, _usualAnchorY.GetValueOrDefault(anchor.Template, anchor.Y));
        }

        var both = await ReadTextVariantsAsync(ItemDialogTwoLineTitleRegion, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("The title of {Item} wraps; the item window is {Shift} px lower.", expectedName, _dialogShiftY);
        // Without the second line it may be a longer-named relative ("... of the Dragon"): then the price has to prove it.
        return both.Any(line => IsSameItemName(secondLine, line))
            ? (ItemTitleCheck.Match, expectedName)
            : (ItemTitleCheck.Unreadable, texts.FirstOrDefault());
    }

    /// <summary>How much lower than usual the item window's contents are; 0 unless it is about a line of text or two.</summary>
    internal static int DialogShift(int anchorY, int usualAnchorY) =>
        anchorY - usualAnchorY is >= 8 and <= 80 and var shift ? shift : 0;

    /// <summary>
    /// When <paramref name="firstLine"/> is the start of a long name up to a word boundary, the rest of the name: what
    /// the second line of the title has to say.
    /// </summary>
    internal static string? SecondTitleLine(string expectedName, string firstLine)
    {
        var words = expectedName.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        for (var count = words.Length - 1; count >= 2; count--)
        {
            var start = string.Join(' ', words[..count]);
            if (start.Length < 24)
            {
                return null; // a title this short does not wrap
            }

            if (NameSimilarity(NormalizeItemName(start), NormalizeItemName(firstLine)) >= 0.9 && IsSameItemName(start, firstLine))
            {
                return string.Join(' ', words[count..]);
            }
        }

        return null;
    }

    /// <summary>
    /// With an unreadable title, the only proof left that the right item is open: the search list showed its name and
    /// the price read live in the dialog is within 10% of the API's price for it. On 22 Nov "Genesis" (title and price
    /// both unreadable) was typed into the dialog of "Gaze", a 77g item.
    /// </summary>
    internal static bool UnreadTitleIsProven(bool searchShowedName, long? livePriceCopper, long apiPriceCopper) =>
        searchShowedName && livePriceCopper is { } live && apiPriceCopper > 0 && Math.Abs(live - apiPriceCopper) * 10 <= apiPriceCopper;

    private static readonly HashSet<string> CutOffEndings = new(StringComparer.Ordinal) { "of", "the", "and", "with", "for", "to", "in", "a", "an" };

    /// <summary>
    /// The dialog cuts long titles: "Soldier's Benthic Waterbreather of the Afflicted" shows as "Soldier's Benthic
    /// Waterbreather of the", and was refused 14 times in one night. A long title (30+ letters) that is the start of
    /// the expected name and ends mid-phrase ("of the") is that item. A title ending in a full word is not: it may be a
    /// whole name of its own ("Polysaturating Reverberating Infusion" against "... Infusion (Purple)").
    /// </summary>
    internal static bool IsCutOffTitleOf(string expectedName, string seenTitle)
    {
        var expected = string.Join(' ', TitleWords(expectedName));
        var seenWords = TitleWords(seenTitle);
        var seen = string.Join(' ', seenWords);
        return seen.Length >= 30 &&
               expected.Length > seen.Length + 1 &&
               expected.StartsWith(seen, StringComparison.Ordinal) &&
               expected[seen.Length] == ' ' &&
               CutOffEndings.Contains(seenWords[^1]);
    }

    /// <summary>Lower-case words of letters and digits (OCR drops apostrophes, not spaces).</summary>
    private static string[] TitleWords(string? text) =>
        new string((text ?? string.Empty).ToLowerInvariant()
                .Where(c => char.IsLetterOrDigit(c) || char.IsWhiteSpace(c))
                .ToArray())
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// True when an OCR title is the expected item. 85% alike overall is not enough on its own: "(Red)" passed for
    /// "(Purple)" and on 22 Nov held 190g in an order for the wrong infusion. Every word has to be recognisably the same
    /// word, and numbers have to agree ("+8 Agony Infusion" is not "+9").
    /// </summary>
    internal static bool IsSameItemName(string expectedName, string seenTitle)
    {
        var similarity = NameSimilarity(NormalizeItemName(expectedName), NormalizeItemName(seenTitle));
        if (similarity < 0.85)
        {
            return false;
        }

        var expected = TitleWords(expectedName);
        var seen = TitleWords(seenTitle);
        if (expected.Length != seen.Length)
        {
            // OCR split or joined a word ("Shaman 5 Etched"): then the letters must agree closely and none may be missing.
            return similarity >= 0.9 && Math.Abs(NormalizeItemName(expectedName).Length - NormalizeItemName(seenTitle).Length) <= 2;
        }

        for (var i = 0; i < expected.Length; i++)
        {
            var (want, got) = (expected[i], seen[i]);
            if (want == got) continue;
            if (want.Any(char.IsDigit) && got.Any(char.IsDigit)) return false;
            if (Math.Max(want.Length, got.Length) >= 4 && NameSimilarity(want, got) < 0.5) return false;
        }

        return true;
    }

    /// <summary>
    /// Compares OCR'd dialog title lines with the expected item name. Any line that is the same name is a match (OCR
    /// often drops an apostrophe or misreads a letter); a readable title that is a different item — including a longer
    /// name that merely contains the expected one — is a mismatch.
    /// </summary>
    internal static (ItemTitleCheck Result, string? SeenTitle) ClassifyItemTitle(string expectedName, IEnumerable<string> ocrTexts)
    {
        var expected = NormalizeItemName(expectedName);
        string? bestOther = null;
        var bestOtherLength = 0;
        foreach (var text in ocrTexts)
        {
            var seen = NormalizeItemName(text);
            if (seen.Length < 3)
            {
                continue;
            }

            if (IsSameItemName(expectedName, text) || IsCutOffTitleOf(expectedName, text))
            {
                return (ItemTitleCheck.Match, text.Trim());
            }

            if (seen.Length > bestOtherLength)
            {
                bestOther = text.Trim();
                bestOtherLength = seen.Length;
            }
        }

        // A short garbage fragment is not evidence of a different item; require a title-sized reading.
        return bestOther is not null && bestOtherLength >= Math.Min(6, expected.Length)
            ? (ItemTitleCheck.Mismatch, bestOther)
            : (ItemTitleCheck.Unreadable, bestOther);
    }

    internal static string NormalizeItemName(string? name) =>
        new((name ?? string.Empty).ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    /// <summary>1 - Levenshtein distance / longer length.</summary>
    internal static double NameSimilarity(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0)
        {
            return 0;
        }

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return 1.0 - (double)previous[b.Length] / Math.Max(a.Length, b.Length);
    }

    private async Task<bool> SelectBuyTabAsync(CancellationToken cancellationToken)
    {
        var buyTab = await WaitForAnyTemplateAsync(
            ["buy_items_tab_open.png", "buy_items_tab.png"],
            BuyTabRegion,
            0.68m,
            _uiWaitMs,
            cancellationToken).ConfigureAwait(false);
        if (buyTab is not null)
        {
            await ClickCenterAndSettleAsync(buyTab, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (_requireTemplateFilesOnDisk && await TradingPostProvenAsync(cancellationToken).ConfigureAwait(false))
        {
            await _input.ClickAsync(DeterministicBuyTabPoint.X, DeterministicBuyTabPoint.Y, cancellationToken).ConfigureAwait(false);
            await SettleAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        return false;
    }

    private async Task GoToRestPositionAsync(CancellationToken cancellationToken)
    {
        var blackLion = await TryFindOnceAsync("black_lion.png", BlackLionCrestRegion, 0.75m, cancellationToken).ConfigureAwait(false);
        if (blackLion is not null)
        {
            await ClickCenterAsync(blackLion, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (_requireTemplateFilesOnDisk)
        {
            await _input.ClickAsync(DeterministicRestPoint.X, DeterministicRestPoint.Y, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Types the name into the search box and waits for the result list. With <paramref name="requireListedName"/>
    /// (cancel flows, which click the first Cancel button) it returns false unless a result row shows the name:
    /// an unconfirmed list may still hold the previous search's rows or the whole unfiltered list.
    /// </summary>
    private async Task<bool> SearchItemAsync(string itemName, CancellationToken cancellationToken, bool requireListedName = false)
    {
        if (requireListedName && CancelSearchGivenUp(itemName))
        {
            return false; // given up for this session (logged when it happened)
        }

        if (await FindSearchFieldAsync(cancellationToken).ConfigureAwait(false) is not { } field)
        {
            return false;
        }

        var (typed, listed) = await TypeSearchAsync(itemName, field, requireListedName, cancellationToken).ConfigureAwait(false);
        if (!typed)
        {
            // 23 Nov: the box kept only the "L" of "Lord Taeres's Shadow", so the list showed everything else.
            _logger.LogInformation("The search box does not hold '{Item}'; typing it once more.", itemName);
            if (await FindSearchFieldAsync(cancellationToken).ConfigureAwait(false) is not { } again)
            {
                return false;
            }

            (_, listed) = await TypeSearchAsync(itemName, again, requireListedName, cancellationToken).ConfigureAwait(false);
        }

        _lastSearchShowedName = listed;

        if (requireListedName && _lastSearchFoundNothing)
        {
            // The game is the fact: the API's order list lags up to ~5 minutes, so an order sold or cancelled (also by
            // hand in game) still shows there. Do not search for it again this session.
            NoteUnconfirmedCancel(itemName, UnconfirmedCancelSearchesBeforeSkip);
            _logger.LogInformation("'{Item}' has no current order or listing in game any more (sold, or cancelled by hand); nothing to cancel, not searching it again for an hour.", itemName);
            return false;
        }

        if (requireListedName && !listed)
        {
            _unconfirmedCancels.Add(itemName);
            var skips = NoteUnconfirmedCancel(itemName, 1);
            _logger.LogInformation(
                "Not cancelling anything for '{Item}': the result list never showed it, so the first Cancel button could belong to another listing.{Note}",
                itemName,
                skips >= UnconfirmedCancelSearchesBeforeSkip ? " Not trying it again for an hour." : string.Empty);
            return false;
        }

        return true;
    }

    /// <summary>
    /// The search box of the Trading Post that is on screen, or null. Never a guessed position: without the TP the same
    /// click and keys land in the game world.
    /// </summary>
    private async Task<(int X, int Y, bool Empty)?> FindSearchFieldAsync(CancellationToken cancellationToken)
    {
        if (!await TradingPostProvenAsync(cancellationToken).ConfigureAwait(false))
        {
            _logger.LogWarning("Not typing a search: the Trading Post is not on screen.");
            return null;
        }

        // Empty boxes score 0.84-1.00 on the placeholder, boxes with text up to 0.68.
        var placeholder = await WaitForTemplateAsync(
            "search.png",
            SearchFieldRegion,
            0.80m,
            Math.Min(_uiWaitMs, Math.Max(40, _uiWaitMs / 3)),
            cancellationToken).ConfigureAwait(false);
        if (placeholder is not null)
        {
            return (CenterX(placeholder), CenterY(placeholder), true);
        }

        // With text in it the box is found by the cog beside it: click near its right end.
        var cog = await WaitForTemplateAsync("cog.png", SearchFieldRegion, 0.70m, _uiWaitMs, cancellationToken).ConfigureAwait(false);
        if (cog is not null)
        {
            return (Math.Max(10, CenterX(cog) - 24), CenterY(cog), false);
        }

        var focused = await TryFindOnceAsync("search_focused.png", SearchFieldRegion, 0.70m, cancellationToken).ConfigureAwait(false);
        return focused is null ? null : (CenterX(focused), CenterY(focused), false);
    }

    /// <summary>
    /// Types the name into the search box. Keys that do something in the world (Backspace is Reply, Enter is chat) are
    /// only pressed once the typed text shows in the box, which proves it has the focus. The Trading Post also searches
    /// without Enter, only a little later.
    /// </summary>
    /// <returns>Whether the box shows the name, and whether the result list does.</returns>
    private async Task<(bool Typed, bool Listed)> TypeSearchAsync(
        string itemName,
        (int X, int Y, bool Empty) field,
        bool requireListedName,
        CancellationToken cancellationToken)
    {
        var name = itemName.Trim();
        _lastSearchFoundNothing = false;
        _lastSearchShowedName = false;
        if (field.Empty)
        {
            await _input.ClickAsync(field.X, field.Y, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _input.TripleClickAsync(field.X, field.Y, cancellationToken).ConfigureAwait(false);
        }

        await SettleShortAsync(cancellationToken).ConfigureAwait(false);
        await _input.SendTextAsync(name, cancellationToken).ConfigureAwait(false);
        await SettleShortAsync(cancellationToken).ConfigureAwait(false);
        var box = await ReadSearchBoxAsync(name, cancellationToken).ConfigureAwait(false);
        if (box == SearchBoxText.Appended || (box == SearchBoxText.HoldsCutOff && !field.Empty))
        {
            // The old text is still in front of it (or may be, out of sight). The box has the focus, so it is safe to clear.
            await _input.KeyPressAsync("end", cancellationToken).ConfigureAwait(false);
            await _input.KeyTapsAsync("backspace", LongestSearchText * 2, cancellationToken).ConfigureAwait(false);
            await SettleShortAsync(cancellationToken).ConfigureAwait(false);
            await _input.SendTextAsync(name, cancellationToken).ConfigureAwait(false);
            await SettleShortAsync(cancellationToken).ConfigureAwait(false);
            box = await ReadSearchBoxAsync(name, cancellationToken).ConfigureAwait(false);
        }

        var typed = box is SearchBoxText.Holds or SearchBoxText.HoldsCutOff;
        if (typed && await TradingPostProvenAsync(cancellationToken).ConfigureAwait(false))
        {
            await _input.KeyPressAsync("enter", cancellationToken).ConfigureAwait(false);
        }

        var listed = await WaitForSearchResultsAsync(itemName, requireListedName, cancellationToken).ConfigureAwait(false);
        return (typed || listed, listed);
    }

    internal enum SearchBoxText
    {
        /// <summary>Nothing readable, or something else: the box may not have the focus.</summary>
        Other,
        /// <summary>The name.</summary>
        Holds,
        /// <summary>The end of the name; the box is too narrow for all of it.</summary>
        HoldsCutOff,
        /// <summary>The name behind older text.</summary>
        Appended
    }

    private async Task<SearchBoxText> ReadSearchBoxAsync(string name, CancellationToken cancellationToken)
    {
        if (_textLocator is null)
        {
            return SearchBoxText.Holds;
        }

        var box = ClassifySearchBox(await ReadTextVariantsAsync(SearchBoxTextRegion, cancellationToken, enlargeMore: true).ConfigureAwait(false), name);
        // Short names only read in a wider crop ("Entropy", 24 Nov).
        return box != SearchBoxText.Other
            ? box
            : ClassifySearchBox(await ReadTextVariantsAsync(SearchBoxWideTextRegion, cancellationToken, enlargeMore: true).ConfigureAwait(false), name);
    }

    internal static SearchBoxText ClassifySearchBox(IEnumerable<string> texts, string name)
    {
        var query = NormalizeItemName(name);
        var best = SearchBoxText.Other;
        foreach (var text in texts.Select(NormalizeItemName).Where(text => text.Length >= Math.Min(3, query.Length)))
        {
            if (NameSimilarity(text, query) >= 0.85)
            {
                return SearchBoxText.Holds;
            }

            if (text.Length >= 12 && text.Length < query.Length && NameSimilarity(text, query[^text.Length..]) >= 0.85)
            {
                best = SearchBoxText.HoldsCutOff;
            }
            else if (best == SearchBoxText.Other && text.Length > query.Length && FuzzyContains(text, query))
            {
                best = SearchBoxText.Appended;
            }
        }

        return best;
    }

    /// <summary>
    /// Recognises a blurred row among our own orders, the only names My Transactions can list: the row must be clearly
    /// closer to the searched name than to any other. "Mini Black Terrier Pup" reads as "Wni Black Terrier", too far off
    /// for a plain match but unmistakable next to 150 other orders; Rune of the Revenant and Renegade stay undecided.
    /// </summary>
    internal static bool IsClosestOwnOrder(string rowText, string normalizedName, IReadOnlyCollection<string> ownNames)
    {
        var target = NameSimilarity(rowText, normalizedName);
        return target >= 0.65 &&
               !ownNames.Any(other => other != normalizedName && NameSimilarity(rowText, other) > target - 0.15);
    }

    /// <summary>
    /// On high ping the previous search's rows stay on screen for a while after Enter, and the "Item" header is
    /// always visible, so without this the next click lands on a stale row (a previous, similarly named item, or
    /// for Outbid Refresh a different order's Cancel button). Polls the result list until a row shows the searched
    /// name; on timeout it carries on (the buy flow still verifies the opened item's title) and logs it.
    /// </summary>
    /// <returns>True when a result row showed the name (or there is no OCR to check with); false otherwise.</returns>
    private async Task<bool> WaitForSearchResultsAsync(string itemName, bool requireListedName, CancellationToken cancellationToken)
    {
        if (_textLocator is null)
        {
            return true;
        }

        var query = NormalizeItemName(itemName);
        var timeoutMs = _requireTemplateFilesOnDisk ? Math.Max(_uiWaitMs, 4_000) : _uiWaitMs;
        var stats = _searchConfirmStats.GetValueOrDefault(query);
        var neverConfirms = UsesShortSearchWait(stats.Seen, stats.Missed);
        if (neverConfirms)
        {
            timeoutMs = Math.Min(timeoutMs, ShortSearchWaitMs);
        }

        var deadline = _utcNow() + TimeSpan.FromMilliseconds(timeoutMs);
        var started = _utcNow();
        while (true)
        {
            await DismissTransientPopupAsync("search", cancellationToken).ConfigureAwait(false);
            if (await TryFindOnceAsync("no_items.png", SearchResultsListRegion, 0.80m, cancellationToken).ConfigureAwait(false) is not null)
            {
                // "Sorry, no items match this filter." — the search answered; there is simply nothing to click.
                _lastSearchFoundNothing = true;
                return false;
            }

            var shown = false;
            foreach (var region in SearchNameRegions(query))
            {
                var texts = await ReadTextVariantsAsync(region, cancellationToken, enlargeMore: region != SearchResultsListRegion).ConfigureAwait(false);
                // Only the first row: its Cancel is the one that gets clicked.
                var ownNames = requireListedName && region != SearchResultsListRegion ? _cancelListNames : null;
                if (texts.Select(NormalizeItemName).Any(text =>
                        FuzzyContains(text, query) || (ownNames is not null && IsClosestOwnOrder(text, query, ownNames))))
                {
                    shown = true;
                    break;
                }
            }

            if (shown)
            {
                _searchConfirmStats[query] = (stats.Seen + 1, stats.Missed);
                var waited = (_utcNow() - started).TotalMilliseconds;
                if (waited > 1_500)
                {
                    _logger.LogInformation("Search results for '{Item}' appeared after {Ms:0} ms.", itemName, waited);
                }

                return true;
            }

            if (_utcNow() >= deadline)
            {
                _searchConfirmStats[query] = (stats.Seen, stats.Missed + 1);
                if (neverConfirms)
                {
                    return false; // rarely confirms on screen; the buy flow still verifies the opened item's title
                }

                _logger.LogInformation(
                    "Search results for '{Item}' were not confirmed within {Ms} ms; continuing (the opened item is still verified).{Note}",
                    itemName,
                    timeoutMs,
                    UsesShortSearchWait(stats.Seen, stats.Missed + 1) ? $" Its name is rarely readable in the results, so later searches wait {ShortSearchWaitMs / 1000} s." : string.Empty);
                await SaveDiagnosticFrameAsync("search-unconfirmed", cancellationToken).ConfigureAwait(false);
                return false;
            }

            await Task.Delay(Math.Max(_pollMs, 250), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Where to read the searched name, narrowest crop that can hold it first.</summary>
    private static IEnumerable<CaptureRegion> SearchNameRegions(string normalizedName)
    {
        if (normalizedName.Length <= 14)
        {
            yield return SearchFirstRowShortRegion;
        }

        if (normalizedName.Length <= 26)
        {
            yield return SearchFirstRowTightRegion;
        }

        yield return SearchFirstRowNameRegion;
        yield return SearchResultsListRegion;
    }

    /// <summary>
    /// Short wait once misses clearly outnumber confirmations. Requiring zero confirmations kept the full 7.5 s wait for
    /// names read only now and then: 222 full waits (~28 min) in the 21-22 Nov 10 h session, Amber Quantic Dipole
    /// alone 13 times. Results that do appear show up in ~1.5 s, inside the short wait.
    /// </summary>
    internal static bool UsesShortSearchWait(int seen, int missed) => missed >= 2 && missed > 2 * seen;

    /// <summary>True when <paramref name="text"/> contains <paramref name="query"/>, allowing ~15% OCR noise.</summary>
    internal static bool FuzzyContains(string text, string query)
    {
        if (query.Length == 0 || text.Length == 0)
        {
            return false;
        }

        if (text.Contains(query, StringComparison.Ordinal))
        {
            return true;
        }

        if (text.Length < query.Length * 0.8)
        {
            return false;
        }

        for (var start = 0; start <= Math.Max(0, text.Length - query.Length); start++)
        {
            var window = text.Substring(start, Math.Min(query.Length, text.Length - start));
            if (NameSimilarity(window, query) >= 0.85)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Replaces a number box's text. Ctrl+A was used before: without the focus the game took its A as "strafe left" and
    /// characters drifted away from the trader. Backspace is the game's Reply key, so callers check that the dialog is on
    /// screen right before. The Deletes come after the text: an emptied number box can fill itself back in ("1") behind
    /// the cursor, which turned an order for 2 into one for 21.
    /// </summary>
    /// <param name="maxExisting">How many characters the field may hold.</param>
    private async Task ReplaceFieldTextAsync(string text, int maxExisting, CancellationToken cancellationToken)
    {
        await _input.KeyPressAsync("end", cancellationToken).ConfigureAwait(false);
        await _input.KeyTapsAsync("backspace", maxExisting, cancellationToken).ConfigureAwait(false);
        await SettleShortAsync(cancellationToken).ConfigureAwait(false);
        await _input.SendTextAsync(text, cancellationToken).ConfigureAwait(false);
        await _input.KeyTapsAsync("delete", maxExisting, cancellationToken).ConfigureAwait(false);
    }
}
