using System.Text.Json;
using Microsoft.Extensions.Logging;
using Mmopal.Contracts;
using Mmopal.Gw2;
using Mmopal.Infrastructure;
using Mmopal.Native;

namespace Mmopal.Worker;

/// <summary>The session's shopping list: loading, live re-pricing, auto re-scan, and the Session-page status lines.</summary>
public sealed partial class WorkerRuntime
{
    private static async Task<IReadOnlyList<ItemCandidate>> ResolveCandidatesAsync(
        TradingReplayRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.CandidatesPath))
        {
            return request.EffectiveCandidates;
        }

        var loaded = await CandidatesLoader.LoadCandidatesAsync(request.CandidatesPath, cancellationToken);
        if (request.EffectiveCandidates.Count == 0)
        {
            return loaded;
        }

        return request.EffectiveCandidates.Concat(loaded).ToArray();
    }

    private static async Task<IReadOnlyList<string>> ResolveBlacklistAsync(
        TradingReplayRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.BlacklistPath))
        {
            return request.EffectiveBlacklist;
        }

        var loaded = await CandidatesLoader.LoadBlacklistAsync(request.BlacklistPath, cancellationToken);
        if (request.EffectiveBlacklist.Count == 0)
        {
            return loaded;
        }

        return request.EffectiveBlacklist.Concat(loaded).ToArray();
    }

    /// <summary>
    /// Re-runs the Market Scanner with the session's filters so a long session follows the market instead of a list
    /// frozen at start. Keeps the old list when the scan fails or finds fewer than 5 items, and saves the new list to
    /// the shopping-list file so the app shows it. Open orders and bag items are unaffected: Outbid Refresh works from
    /// the account's orders and the sell pass from everything Mmopal ordered.
    /// </summary>
    private async Task<IReadOnlyList<ItemCandidate>?> RescanShoppingListAsync(
        MarketScanFilter filter,
        IReadOnlyList<string> blacklist,
        int previousCount,
        string? candidatesPath,
        CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Auto re-scan: scanning the market with the session's filters...");
            var results = await _marketScanner!.ScanAsync(filter, blacklist, progress: null, cancellationToken).ConfigureAwait(false);
            if (results.Count < 5)
            {
                _logger.LogWarning("Auto re-scan found only {Count} item(s); keeping the current {Previous}-item list.", results.Count, previousCount);
                return null;
            }

            var fresh = results.Select(item => item.ToCandidate()).ToArray();
            if (!string.IsNullOrWhiteSpace(candidatesPath) && candidatesPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                OperatorListsStore.SaveShoppingList(candidatesPath, fresh);
            }

            _logger.LogInformation(
                "Auto re-scan: shopping list updated to {Count} items (was {Previous}). Top: {Top}",
                fresh.Length,
                previousCount,
                string.Join(", ", results.Take(3).Select(item => item.ItemName)));
            return fresh;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("Auto re-scan failed ({Message}); keeping the current list.", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Start the re-scan clock at the scanned list's file time, so a list saved hours or days ago is refreshed on the
    /// first cycle instead of trading on stale prices until the first interval passes.
    /// </summary>
    internal static DateTimeOffset InitialRescanClock(TradingReplayRequest request, DateTimeOffset nowUtc)
    {
        if (request.AutoRescanFilter is null || request.AutoRescanIntervalMinutes <= 0 ||
            string.IsNullOrWhiteSpace(request.CandidatesPath) || !File.Exists(request.CandidatesPath))
        {
            return nowUtc;
        }

        var written = new DateTimeOffset(File.GetLastWriteTimeUtc(request.CandidatesPath), TimeSpan.Zero);
        return written < nowUtc ? written : nowUtc;
    }

    internal static IReadOnlyList<ItemCandidate> MergeWithCustomList(IReadOnlyList<ItemCandidate> custom, IReadOnlyList<ItemCandidate> scanned)
    {
        if (custom.Count == 0)
        {
            return scanned;
        }

        var customNames = custom
            .Where(c => !string.IsNullOrWhiteSpace(c.ItemName))
            .Select(c => ItemNameNormalizer.Normalize(c.ItemName))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return custom
            .Concat(scanned.Where(c => !customNames.Contains(ItemNameNormalizer.Normalize(c.ItemName))))
            .ToArray();
    }

    /// <summary>One line for the Session page: list size and when the next market re-scan happens (or why it is off).</summary>
    internal static string DescribeMarketList(TradingReplayRequest request, int listCount, int scannedCount, DateTimeOffset lastRescanUtc, DateTimeOffset nowUtc)
    {
        if (request.AutoRescanFilter is null || request.AutoRescanIntervalMinutes <= 0)
        {
            return $"{listCount} items · re-scan off ({(request.AutoRescanIntervalMinutes <= 0 && request.AutoRescanFilter is not null ? "0 h" : "list is not the scanner's")})";
        }

        var due = lastRescanUtc.AddMinutes(request.AutoRescanIntervalMinutes) - nowUtc;
        var next = due <= TimeSpan.Zero
            ? "re-scan now"
            : due.TotalHours >= 1 ? $"re-scan in {(int)due.TotalHours}h {due.Minutes:00}m" : $"re-scan in {Math.Max(1, (int)Math.Ceiling(due.TotalMinutes))}m";
        return $"{listCount} items ({scannedCount} scanned) · {next}";
    }

    internal static string DescribePortfolio(TradingReplayRequest request, PortfolioGuidance? guidance, string? syncNote)
    {
        if (!request.UsePortfolioIntelligence)
        {
            return "History off";
        }

        if (guidance is null)
        {
            return "History: loading";
        }

        var boosts = guidance.ByItemName.Values.Count(a => !a.ShouldSkip && a.ScoreMultiplier > 1.0);
        var losers = guidance.LosingItems?.Count ?? 0;
        var stacks = guidance.ByItemName.Values.Count(a => a.ShouldSkip && a.Reason?.Contains("sell stack", StringComparison.OrdinalIgnoreCase) == true);
        var text = $"History: {boosts} boosted · {losers} skipped (lost money)";
        if (stacks > 0) text += $" · {stacks} stacked";
        return syncNote is null ? text : $"{text} · {syncNote}";
    }

    private async Task<IReadOnlyList<ItemCandidate>> RefreshCycleCandidatesAsync(
        IReadOnlyList<ItemCandidate> baselineCandidates,
        Dictionary<string, int> resolvedItemIdCache,
        LiveCandidateProfitRefresher refresher,
        ProfitabilityPolicy policy,
        int overbidMinCopper,
        int undercutMinCopper,
        PortfolioGuidance? portfolioGuidance,
        CancellationToken cancellationToken)
    {
        if (baselineCandidates.Count == 0)
        {
            return baselineCandidates;
        }

        if (_marketData is not Gw2OfficialDataProvider officialData)
        {
            if (portfolioGuidance is null)
            {
                return baselineCandidates;
            }

            var offlineResult = refresher.Refresh(
                baselineCandidates,
                new Dictionary<int, Gw2LivePriceQuote>(),
                policy,
                overbidMinCopper,
                undercutMinCopper,
                portfolioGuidance);
            return offlineResult.Candidates;
        }

        try
        {
            var unresolvedNames = baselineCandidates
                .Where(c => c.ItemId is null && !string.IsNullOrWhiteSpace(c.ItemName) && !resolvedItemIdCache.ContainsKey(c.ItemName.Trim()))
                .Select(c => c.ItemName.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (unresolvedNames.Length > 0 && _ledgerStore is not null)
            {
                var found = await _ledgerStore.GetItemIdsByNamesAsync(unresolvedNames, cancellationToken).ConfigureAwait(false);
                foreach (var (name, id) in found)
                {
                    resolvedItemIdCache[name] = id;
                }
            }

            var enriched = new ItemCandidate[baselineCandidates.Count];
            var itemIds = new List<int>(baselineCandidates.Count);
            for (var i = 0; i < baselineCandidates.Count; i++)
            {
                var c = baselineCandidates[i];
                if (c.ItemId is { } id && id > 0)
                {
                    enriched[i] = c;
                    itemIds.Add(id);
                }
                else if (!string.IsNullOrWhiteSpace(c.ItemName) && resolvedItemIdCache.TryGetValue(c.ItemName.Trim(), out var cachedId) && cachedId > 0)
                {
                    enriched[i] = c with { ItemId = cachedId };
                    itemIds.Add(cachedId);
                }
                else
                {
                    enriched[i] = c;
                }
            }

            var distinctIds = itemIds.Distinct().ToArray();
            if (distinctIds.Length == 0)
            {
                return portfolioGuidance is null
                    ? baselineCandidates
                    : refresher.Refresh(baselineCandidates, new Dictionary<int, Gw2LivePriceQuote>(), policy, overbidMinCopper, undercutMinCopper, portfolioGuidance).Candidates;
            }

            var quotes = await officialData.GetLiveMarketQuotesAsync(distinctIds, cancellationToken).ConfigureAwait(false);
            var result = refresher.Refresh(enriched, quotes, policy, overbidMinCopper, undercutMinCopper, portfolioGuidance);
            var cycleInfo = Gw2MarketCycle.Describe(_utcNow());
            var portfolioNote = portfolioGuidance is not null && (result.PortfolioSkippedCount > 0 || result.PortfolioBoostedCount > 0)
                ? $", {result.PortfolioBoostedCount} winner-boosted, {result.PortfolioSkippedCount} portfolio-guarded"
                : string.Empty;
            _logger.LogInformation(
                "GW2 Live Pulse ({Phase}): {Refreshed} profitable, {Dropped} spread-collapsed dropped, {Unquoted} unquoted{PortfolioNote}. {Top}",
                cycleInfo.PhaseName,
                result.RefreshedCount,
                result.DroppedCollapsedCount,
                result.RetainedUnquotedCount,
                portfolioNote,
                result.TopPickSummary);
            return result.Candidates;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Live API profit refresh failed; using cached candidate spreads for this cycle.");
            return baselineCandidates;
        }
    }
}
