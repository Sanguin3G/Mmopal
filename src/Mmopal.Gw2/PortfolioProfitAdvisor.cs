using Mmopal.Contracts;

namespace Mmopal.Gw2;

public sealed record PortfolioItemAdvice(
    double ScoreMultiplier,
    bool ShouldSkip,
    string? Reason);

public sealed record PortfolioGuidance(
    IReadOnlyDictionary<int, PortfolioItemAdvice> ByItemId,
    IReadOnlyDictionary<string, PortfolioItemAdvice> ByItemName,
    string MarketPhase,
    bool IsOffPeak,
    string Summary)
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

        if (ledgerHistory.Count > 0)
        {
            var report = TradingIncomeAnalytics.Analyze(ledgerHistory);
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

                PortfolioItemAdvice? advice = null;
                if ((row.UnitsFlipped >= 2 && row.NetProfitCopper < 0) ||
                    (row.UnitsFlipped >= 1 && row.RoiPercent <= -5m))
                {
                    advice = new PortfolioItemAdvice(0.0, ShouldSkip: true, "Chronic FIFO loss in ledger");
                    guarded++;
                }
                else if (row.AvgHoldHours >= 72.0)
                {
                    advice = new PortfolioItemAdvice(0.55, ShouldSkip: false, "Slow turnover (72h+ avg hold)");
                }
                else if (row.UnitsFlipped >= 2 && row.NetProfitCopper > 0 && row.AvgHoldHours <= 24.0 && row.RoiPercent >= 12m)
                {
                    advice = new PortfolioItemAdvice(1.40, ShouldSkip: false, "Fast proven FIFO winner");
                    winners++;
                }
                else if (row.UnitsFlipped >= 2 && row.NetProfitCopper > 0 && row.AvgHoldHours <= 48.0)
                {
                    advice = new PortfolioItemAdvice(1.22, ShouldSkip: false, "Proven FIFO winner");
                    winners++;
                }

                if (advice is null)
                {
                    continue;
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

        return new PortfolioGuidance(byId, byName, cycle.PhaseName, isOffPeak, summary);
    }

    public static string BuildDashboardInsightSummary(
        TradingIncomeReport report,
        IReadOnlyList<Gw2ActiveOrder> activeOrders,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(activeOrders);

        var parts = new List<string>();
        var topWinner = report.ItemLeaderboard.FirstOrDefault(r => r.NetProfitCopper > 0 && r.UnitsFlipped > 0);
        if (topWinner is not null)
        {
            parts.Add($"Top flipper: {topWinner.ItemName} (+{MarketScanItem.FormatCoins(topWinner.NetProfitCopper)}, {topWinner.AvgHoldFormatted} hold)");
        }

        var loserCount = report.ItemLeaderboard.Count(r =>
            (r.UnitsFlipped >= 2 && r.NetProfitCopper < 0) ||
            (r.UnitsFlipped >= 1 && r.RoiPercent <= -5m));
        if (loserCount > 0)
        {
            parts.Add($"Auto-guarded losers: {loserCount}");
        }

        var bestHour = report.HourlySeries
            .Where(h => h.RealizedProfitCopper > 0)
            .OrderByDescending(h => h.RealizedProfitCopper)
            .FirstOrDefault();
        if (bestHour is not null)
        {
            parts.Add($"Peak local hour: {bestHour.LocalHour:00}:00");
        }

        var overExposed = activeOrders
            .Where(o => o.Kind == TransactionType.Sell)
            .GroupBy(o => ItemNameNormalizer.Normalize(o.ItemName), StringComparer.OrdinalIgnoreCase)
            .Count(g => g.Count() >= 2 || g.Sum(x => Math.Max(1L, x.Quantity)) >= 2);
        if (overExposed > 0)
        {
            parts.Add($"Heavy sell stacks held: {overExposed}");
        }

        var cycle = Gw2MarketCycle.Describe(nowUtc);
        parts.Add($"Phase: {cycle.PhaseName}");
        return parts.Count == 1
            ? $"Live Advisor ready ({cycle.PhaseName}) — sync trades to unlock per-item FIFO winner boosts & loser guards."
            : string.Join("  •  ", parts);
    }
}
