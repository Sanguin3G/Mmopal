using System.Globalization;
using Mmopal.Contracts;

namespace Mmopal.Gw2;

public sealed record PortfolioItemAdvice(
    double ScoreMultiplier,
    bool ShouldSkip,
    string? Reason)
{
    /// <summary>Short label for tables: "Skipped: …", "Boost ×1.4", "Slow ×0.55".</summary>
    public string ShortLabel
    {
        get
        {
            if (ShouldSkip)
            {
                return Reason?.Contains("sell stack", StringComparison.OrdinalIgnoreCase) == true
                    ? "Skipped: stack listed"
                    : "Skipped: lost money";
            }

            return ScoreMultiplier >= 1.0
                ? string.Create(CultureInfo.InvariantCulture, $"Boost ×{ScoreMultiplier:0.##}")
                : string.Create(CultureInfo.InvariantCulture, $"Slow ×{ScoreMultiplier:0.##}");
        }
    }
}

public sealed record PortfolioGuidance(
    IReadOnlyDictionary<int, PortfolioItemAdvice> ByItemId,
    IReadOnlyDictionary<string, PortfolioItemAdvice> ByItemName,
    string MarketPhase,
    bool IsOffPeak,
    string Summary,
    // Items skipped because they lost money in the last 30 days (not transient sell-stack guards).
    IReadOnlyList<string>? LosingItems = null)
{
    public PortfolioItemAdvice? TryGetAdvice(int? itemId, string? itemName)
    {
        if (itemId is { } id && id > 0 && ByItemId.TryGetValue(id, out var byId))
        {
            return byId;
        }

        if (!string.IsNullOrWhiteSpace(itemName) &&
            ByItemName.TryGetValue(ItemNameNormalizer.Normalize(itemName), out var byName))
        {
            return byName;
        }

        return null;
    }
}

/// <summary>
/// Converts permanent SQLite FIFO transaction ledger performance, active TP exposure,
/// and global NA/EU market cycle timing into actionable ranking boosts and loss/bagholder guards.
/// </summary>
public static class PortfolioProfitAdvisor
{
    /// <summary>Only the last 30 days of trades count: an item that lost money months ago may be fine now.</summary>
    public const int HistoryWindowDays = 30;

    /// <summary>At least two units flipped at a net loss. One bad flip is noise (an undercut war, a spike).</summary>
    public static bool IsChronicLoser(ItemPnlRow row) => row.UnitsFlipped >= 2 && row.NetProfitCopper < 0;

    public static PortfolioGuidance BuildGuidance(
        IReadOnlyList<Gw2LedgerTransaction> ledgerHistory,
        IReadOnlyList<Transaction> activeOrders,
        DateTimeOffset nowUtc,
        long defaultOrderQuantity = 1)
    {
        ArgumentNullException.ThrowIfNull(ledgerHistory);
        ArgumentNullException.ThrowIfNull(activeOrders);

        var byId = new Dictionary<int, PortfolioItemAdvice>();
        var byName = new Dictionary<string, PortfolioItemAdvice>(StringComparer.OrdinalIgnoreCase);
        var nameToId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var winners = 0;
        var guarded = 0;
        var losingItems = new List<string>();
        var windowStart = nowUtc.AddDays(-HistoryWindowDays);
        var recentHistory = ledgerHistory.Where(t => t.PurchasedAt >= windowStart).ToArray();

        if (recentHistory.Length > 0)
        {
            var report = TradingIncomeAnalytics.Analyze(recentHistory);
            foreach (var row in report.ItemLeaderboard)
            {
                if (row.UnitsFlipped <= 0)
                {
                    continue;
                }

                var norm = ItemNameNormalizer.Normalize(row.ItemName);
                if (row.ItemId > 0 && !string.IsNullOrEmpty(norm))
                {
                    nameToId[norm] = row.ItemId;
                }

                var advice = AdviceFromHistory(row);
                if (advice is null)
                {
                    continue;
                }

                if (advice.ShouldSkip)
                {
                    guarded++;
                    losingItems.Add(row.ItemName);
                }
                else if (advice.ScoreMultiplier > 1.0)
                {
                    winners++;
                }

                if (row.ItemId > 0)
                {
                    byId[row.ItemId] = advice;
                }

                if (!string.IsNullOrEmpty(norm))
                {
                    byName[norm] = advice;
                }
            }
        }

        var exposureThreshold = Math.Max(2L, Math.Max(1L, defaultOrderQuantity) * 2);
        var sellGroups = activeOrders
            .Where(o => o.Type == TransactionType.Sell && !string.IsNullOrWhiteSpace(o.ItemName))
            .GroupBy(o => ItemNameNormalizer.Normalize(o.ItemName), StringComparer.OrdinalIgnoreCase);
        var exposureSkipped = 0;

        foreach (var group in sellGroups)
        {
            var totalQty = group.Sum(o => Math.Max(1L, o.Quantity));
            var count = group.Count();
            if (totalQty < exposureThreshold && count < 2)
            {
                continue;
            }

            var advice = new PortfolioItemAdvice(0.0, ShouldSkip: true, "Already holding unsold sell stack on TP");
            byName[group.Key] = advice;
            if (nameToId.TryGetValue(group.Key, out var knownId) && knownId > 0)
            {
                byId[knownId] = advice;
            }

            exposureSkipped++;
        }

        var cycle = Gw2MarketCycle.Describe(nowUtc);
        var isOffPeak = string.Equals(cycle.PhaseName, "Global Off-Peak", StringComparison.OrdinalIgnoreCase);
        var summary = $"{cycle.PhaseName} • {winners} winner boost(s), {guarded} loss guard(s), {exposureSkipped} stacked-sell guard(s)";

        return new PortfolioGuidance(byId, byName, cycle.PhaseName, isOffPeak, summary, losingItems);
    }

    /// <summary>Loss guard, slow-turnover penalty or winner boost from an item's recent FIFO results; null when neutral.</summary>
    private static PortfolioItemAdvice? AdviceFromHistory(ItemPnlRow row)
    {
        if (IsChronicLoser(row))
        {
            return new PortfolioItemAdvice(0.0, ShouldSkip: true, $"Lost money on {row.UnitsFlipped} flips in the last {HistoryWindowDays} days");
        }

        if (row.AvgHoldHours >= 72.0)
        {
            return new PortfolioItemAdvice(0.55, ShouldSkip: false, "Slow turnover (72h+ avg hold)");
        }

        var provenWinner = row.UnitsFlipped >= 2 && row.NetProfitCopper > 0;
        if (provenWinner && row.AvgHoldHours <= 24.0 && row.RoiPercent >= 12m)
        {
            return new PortfolioItemAdvice(1.40, ShouldSkip: false, "Fast proven FIFO winner");
        }

        if (provenWinner && row.AvgHoldHours <= 48.0)
        {
            return new PortfolioItemAdvice(1.22, ShouldSkip: false, "Proven FIFO winner");
        }

        return null;
    }

    public static string BuildDashboardInsightSummary(
        TradingIncomeReport report,
        IReadOnlyList<Gw2ActiveOrder> activeOrders,
        DateTimeOffset nowUtc,
        IReadOnlyList<RunningHourPoint>? runningHours = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(activeOrders);

        var parts = new List<string>();
        var topWinner = report.ItemLeaderboard.FirstOrDefault(r => r.NetProfitCopper > 0 && r.UnitsFlipped > 0);
        if (topWinner is not null)
        {
            parts.Add($"Best item: {topWinner.ItemName} (+{MarketScanItem.FormatCoins(topWinner.NetProfitCopper)}, sells in ~{topWinner.AvgHoldFormatted})");
        }

        var loserCount = report.ItemLeaderboard.Count(IsChronicLoser);
        if (loserCount > 0)
        {
            parts.Add($"Skipped for losing money: {loserCount}");
        }

        // Totals per hour only show when Mmopal ran, so the best hour needs running times.
        if (RunningHours.Best(runningHours ?? []) is { } bestHour)
        {
            parts.Add($"Best hour: {bestHour.LocalHour:00}:00 ({MarketScanItem.FormatCoins(bestHour.SoldNetPerHourCopper)} sold per hour run)");
        }

        var overExposed = activeOrders
            .Where(o => o.Kind == TransactionType.Sell)
            .GroupBy(o => ItemNameNormalizer.Normalize(o.ItemName), StringComparer.OrdinalIgnoreCase)
            .Count(g => g.Count() >= 2 || g.Sum(x => Math.Max(1L, x.Quantity)) >= 2);
        if (overExposed > 0)
        {
            parts.Add($"Items listed in several stacks: {overExposed}");
        }

        var cycle = Gw2MarketCycle.Describe(nowUtc);
        parts.Add($"Market now: {cycle.PhaseName}");
        return parts.Count == 1
            ? $"Live Advisor ready ({cycle.PhaseName}) — sync trades to unlock per-item FIFO winner boosts & loser guards."
            : string.Join("  •  ", parts);
    }
}
