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

        var texts = await ReadTextVariantsAsync(ItemDialogTitleRegion, cancellationToken).ConfigureAwait(false);
        return ClassifyItemTitle(expectedName, texts);
    }

    /// <summary>
    /// Compares OCR'd dialog title lines with the expected item name. Any line close to the expected name is a match
    /// (OCR often drops an apostrophe or misreads a letter); a readable title that is clearly a different item —
    /// including a longer name that merely contains the expected one — is a mismatch.
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

            if (NameSimilarity(expected, seen) >= 0.85)
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
            await ClickCenterAsync(buyTab, cancellationToken).ConfigureAwait(false);
            await SettleAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (_requireTemplateFilesOnDisk)
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

    private async Task<bool> SearchItemAsync(string itemName, CancellationToken cancellationToken)
    {
        var searchField = await WaitForAnyTemplateAsync(
            ["search.png", "search_focused.png"],
            SearchFieldRegion,
            0.70m,
            Math.Min(_uiWaitMs, Math.Max(40, _uiWaitMs / 3)),
            cancellationToken).ConfigureAwait(false);

        if (searchField is not null)
        {
            await ClickCenterAsync(searchField, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // When text is already in the search input, click near the right end of the search box
            // so the cursor never lands mid-word if selection fails.
            var cog = await WaitForTemplateAsync("cog.png", SearchFieldRegion, 0.70m, _uiWaitMs, cancellationToken).ConfigureAwait(false);
            if (cog is not null)
            {
                await _input.ClickAsync(Math.Max(10, CenterX(cog) - 24), CenterY(cog), cancellationToken).ConfigureAwait(false);
            }
            else if (_requireTemplateFilesOnDisk)
            {
                await _input.ClickAsync(DeterministicSearchBoxPoint.X, DeterministicSearchBoxPoint.Y, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                return false;
            }
        }

        await ReplaceFieldTextAsync(itemName.Trim(), cancellationToken).ConfigureAwait(false);
        await SettleShortAsync(cancellationToken).ConfigureAwait(false);
        await _input.KeyPressAsync("enter", cancellationToken).ConfigureAwait(false);
        await WaitForSearchResultsAsync(itemName, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// On high ping the previous search's rows stay on screen for a while after Enter, and the "Item" header is
    /// always visible, so without this the next click lands on a stale row (a previous, similarly named item, or
    /// for Outbid Refresh a different order's Cancel button). Polls the result list until a row shows the searched
    /// name; on timeout it carries on (the buy flow still verifies the opened item's title) and logs it.
    /// </summary>
    private async Task WaitForSearchResultsAsync(string itemName, CancellationToken cancellationToken)
    {
        if (_textLocator is null)
        {
            return;
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
                return;
            }

            var texts = await ReadTextVariantsAsync(SearchResultsListRegion, cancellationToken).ConfigureAwait(false);
            if (texts.Any(text => FuzzyContains(NormalizeItemName(text), query)))
            {
                _searchConfirmStats[query] = (stats.Seen + 1, stats.Missed);
                var waited = (_utcNow() - started).TotalMilliseconds;
                if (waited > 1_500)
                {
                    _logger.LogInformation("Search results for '{Item}' appeared after {Ms:0} ms.", itemName, waited);
                }

                return;
            }

            if (_utcNow() >= deadline)
            {
                _searchConfirmStats[query] = (stats.Seen, stats.Missed + 1);
                if (neverConfirms)
                {
                    return; // known to never confirm on screen; the opened item's title is still verified
                }

                _logger.LogInformation(
                    "Search results for '{Item}' were not confirmed within {Ms} ms; continuing (the opened item is still verified).{Note}",
                    itemName,
                    timeoutMs,
                    UsesShortSearchWait(stats.Seen, stats.Missed + 1) ? $" Its name never shows up readably, so later searches wait {ShortSearchWaitMs / 1000} s." : string.Empty);
                await SaveDiagnosticFrameAsync("search-unconfirmed", cancellationToken).ConfigureAwait(false);
                return;
            }

            await Task.Delay(Math.Max(_pollMs, 250), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Two misses and no confirmation yet: the name does not read on screen, so waiting longer will not help.</summary>
    internal static bool UsesShortSearchWait(int seen, int missed) => seen == 0 && missed >= 2;

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

    private async Task ReplaceFieldTextAsync(string text, CancellationToken cancellationToken)
    {
        await _input.KeyPressAsync("ctrl+a", cancellationToken).ConfigureAwait(false);
        await SettleShortAsync(cancellationToken).ConfigureAwait(false);
        await _input.KeyPressAsync("backspace", cancellationToken).ConfigureAwait(false);
        await SettleShortAsync(cancellationToken).ConfigureAwait(false);
        await _input.KeyPressAsync("ctrl+a", cancellationToken).ConfigureAwait(false);
        await SettleShortAsync(cancellationToken).ConfigureAwait(false);
        await _input.KeyPressAsync("backspace", cancellationToken).ConfigureAwait(false);
        await SettleShortAsync(cancellationToken).ConfigureAwait(false);
        await _input.SendTextAsync(text, cancellationToken).ConfigureAwait(false);
    }
}
