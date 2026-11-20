using Mmopal.Contracts;

namespace Mmopal.Gw2;

public sealed record LiveCandidateRefreshResult(
    IReadOnlyList<ItemCandidate> Candidates,
    int RefreshedCount,
    int DroppedCollapsedCount,
    int RetainedUnquotedCount,
    string TopPickSummary,
    int PortfolioSkippedCount = 0,
    int PortfolioBoostedCount = 0);

/// <summary>
/// Re-prices trading candidates against live GW2 /v2/commerce/prices quotes, drops items whose
/// spread collapsed below the 15% tax/profit floor or lost order-book depth, applies personal
/// FIFO portfolio boosts/guards, and re-ranks surviving candidates by ROI weighted by liquidity.
/// </summary>
public sealed class LiveCandidateProfitRefresher
{
    private readonly ProfitabilityEngine _profitability = new();

    public LiveCandidateRefreshResult Refresh(
        IReadOnlyList<ItemCandidate> candidates,
        IReadOnlyDictionary<int, Gw2LivePriceQuote> quotes,
        ProfitabilityPolicy policy,
        int overbidCopper = 1,
        int undercutCopper = 1,
        PortfolioGuidance? portfolioGuidance = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(quotes);
        ArgumentNullException.ThrowIfNull(policy);

        if (candidates.Count == 0)
        {
            return new LiveCandidateRefreshResult(
                Array.Empty<ItemCandidate>(),
                RefreshedCount: 0,
                DroppedCollapsedCount: 0,
                RetainedUnquotedCount: 0,
                TopPickSummary: string.Empty);
        }

        var safeOverbid = Math.Max(0, overbidCopper);
        var safeUndercut = Math.Max(0, undercutCopper);
        var liveScored = new List<(ItemCandidate Candidate, double Score, int OriginalIndex, Gw2LivePriceQuote Quote, ProfitabilityResult Profit)>();
        var unquoted = new List<ItemCandidate>();
        var droppedCount = 0;
        var portfolioSkippedCount = 0;
        var portfolioBoostedCount = 0;

        for (var i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            var advice = portfolioGuidance?.TryGetAdvice(candidate.ItemId, candidate.ItemName);
            if (advice is { ShouldSkip: true })
            {
                portfolioSkippedCount++;
                continue;
            }

            if (candidate.ItemId is not { } itemId || !quotes.TryGetValue(itemId, out var quote))
            {
                unquoted.Add(candidate);
                continue;
            }

            if (quote.HighestBuyCopper <= 0 ||
                quote.LowestSellCopper <= 0 ||
                quote.BuyQuantity <= 0 ||
                quote.SellQuantity <= 0)
            {
                droppedCount++;
                continue;
            }

            var liveBuyPrice = quote.HighestBuyCopper + safeOverbid;
            var liveSellPrice = Math.Max(1L, quote.LowestSellCopper - safeUndercut);
            if (liveBuyPrice >= liveSellPrice ||
                (candidate.MaximumPriceCopper is { } itemMaxBuy && itemMaxBuy > 0 && liveBuyPrice > itemMaxBuy))
            {
                droppedCount++;
                continue;
            }

            var eval = _profitability.Evaluate(
                liveBuyPrice,
                liveSellPrice,
                policy,
                candidate.MinimumProfitPercentage);
            if (!eval.IsValid || !eval.IsProfitable)
            {
                droppedCount++;
                continue;
            }

            var refreshed = candidate with
            {
                BuyPriceCopper = liveBuyPrice,
                SellPriceCopper = liveSellPrice,
                Roi = decimal.Round(eval.ProfitPercentage, 2)
            };

            var minDepth = Math.Min(quote.BuyQuantity, quote.SellQuantity);
            var depthWeight = portfolioGuidance is { IsOffPeak: true }
                ? Math.Pow(Math.Log10(10d + minDepth), 1.35)
                : Math.Log10(10d + minDepth);
            var multiplier = advice?.ScoreMultiplier ?? 1.0;
            if (multiplier > 1.01)
            {
                portfolioBoostedCount++;
            }

            var liquidityScore = (double)eval.ProfitPercentage * depthWeight * multiplier;
            liveScored.Add((refreshed, liquidityScore, i, quote, eval));
        }

        var ordered = liveScored
            .OrderByDescending(entry => entry.Score)
            .ThenBy(entry => entry.OriginalIndex)
            .ToArray();

        var combined = new List<ItemCandidate>(ordered.Length + unquoted.Count);
        foreach (var entry in ordered)
        {
            combined.Add(entry.Candidate);
        }

        combined.AddRange(unquoted);

        var topSummary = ordered.Length > 0
            ? $"Top: {ordered[0].Candidate.ItemName} ({ordered[0].Profit.ProfitPercentage:0.0}% ROI, {ordered[0].Quote.BuyQuantity} buy / {ordered[0].Quote.SellQuantity} sell)"
            : string.Empty;

        return new LiveCandidateRefreshResult(
            combined,
            RefreshedCount: ordered.Length,
            DroppedCollapsedCount: droppedCount,
            RetainedUnquotedCount: unquoted.Count,
            TopPickSummary: topSummary,
            PortfolioSkippedCount: portfolioSkippedCount,
            PortfolioBoostedCount: portfolioBoostedCount);
    }
}
