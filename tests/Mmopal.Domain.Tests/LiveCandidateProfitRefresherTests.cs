using Mmopal.Contracts;
using Mmopal.Gw2;
using Xunit;

namespace Mmopal.Domain.Tests;

public sealed class LiveCandidateProfitRefresherTests
{
    [Fact]
    public void Refresh_UpdatesPricesFiltersCollapsedSpreadsAndRanksByRoiTimesLiquidity()
    {
        var refresher = new LiveCandidateProfitRefresher();
        var policy = new ProfitabilityPolicy(MinimumProfitPercentage: 10m);

        var candidates = new[]
        {
            // High ROI (60%+) with strong depth -> should rank #1
            new ItemCandidate("Liquid High Flip", Quantity: 2, SellPriceCopper: 20_000, BuyPriceCopper: 10_000, ItemId: 101),
            // Spread collapsed below 15% tax -> should be dropped
            new ItemCandidate("Collapsed Spread Item", Quantity: 1, SellPriceCopper: 20_000, BuyPriceCopper: 10_000, ItemId: 102),
            // Zero buy depth -> should be dropped
            new ItemCandidate("Dead Book Item", Quantity: 1, SellPriceCopper: 20_000, BuyPriceCopper: 10_000, ItemId: 103),
            // Higher liquidity than #4 if equal ROI, or strong moderate ROI with huge depth -> test ranking
            new ItemCandidate("Steady High Volume Flip", Quantity: 5, SellPriceCopper: 20_000, BuyPriceCopper: 10_000, ItemId: 104),
            // No ItemId -> retained at end unchanged
            new ItemCandidate("Manual Unresolved Item", Quantity: 1, SellPriceCopper: 25_000, BuyPriceCopper: 12_000)
        };

        var quotes = new Dictionary<int, Gw2LivePriceQuote>
        {
            [101] = new(101, HighestBuyCopper: 10_000, BuyQuantity: 100, LowestSellCopper: 20_000, SellQuantity: 150),
            [102] = new(102, HighestBuyCopper: 10_000, BuyQuantity: 50, LowestSellCopper: 11_000, SellQuantity: 50),
            [103] = new(103, HighestBuyCopper: 10_000, BuyQuantity: 0, LowestSellCopper: 25_000, SellQuantity: 25),
            [104] = new(104, HighestBuyCopper: 10_000, BuyQuantity: 2_000, LowestSellCopper: 21_500, SellQuantity: 3_000)
        };

        var result = refresher.Refresh(candidates, quotes, policy, overbidCopper: 1, undercutCopper: 1);

        Assert.Equal(2, result.RefreshedCount);
        Assert.Equal(2, result.DroppedCollapsedCount);
        Assert.Equal(1, result.RetainedUnquotedCount);
        Assert.Equal(3, result.Candidates.Count);

        // Item 104 has both higher live ROI (~82.7%) AND higher depth (min 2000), so it must lead ahead of Item 101 (~70.0%) and Manual Unresolved Item
        Assert.Equal("Steady High Volume Flip", result.Candidates[0].ItemName);
        Assert.Equal(10_001, result.Candidates[0].BuyPriceCopper);
        Assert.Equal(21_499, result.Candidates[0].SellPriceCopper);
        Assert.True(result.Candidates[0].Roi > 80m);

        Assert.Equal("Liquid High Flip", result.Candidates[1].ItemName);
        Assert.Equal(10_001, result.Candidates[1].BuyPriceCopper);
        Assert.Equal(19_999, result.Candidates[1].SellPriceCopper);

        Assert.Equal("Manual Unresolved Item", result.Candidates[2].ItemName);
        Assert.DoesNotContain(result.Candidates, c => c.ItemName is "Collapsed Spread Item" or "Dead Book Item");
        Assert.Contains("Steady High Volume Flip", result.TopPickSummary);
    }

    [Fact]
    public void Refresh_DropsItemWhenOverbidExceedsPerCandidateMaximumPrice()
    {
        var refresher = new LiveCandidateProfitRefresher();
        var policy = new ProfitabilityPolicy(MinimumProfitPercentage: 5m);
        var candidates = new[]
        {
            new ItemCandidate("Capped Item", Quantity: 1, MaximumPriceCopper: 10_000, SellPriceCopper: 25_000, BuyPriceCopper: 9_900, ItemId: 201)
        };
        var quotes = new Dictionary<int, Gw2LivePriceQuote>
        {
            [201] = new(201, HighestBuyCopper: 10_000, BuyQuantity: 40, LowestSellCopper: 25_000, SellQuantity: 40)
        };

        var result = refresher.Refresh(candidates, quotes, policy, overbidCopper: 1, undercutCopper: 1);

        Assert.Empty(result.Candidates);
        Assert.Equal(1, result.DroppedCollapsedCount);
    }

    [Fact]
    public void PortfolioProfitAdvisor_BoostsFastWinnersGuardsChronicLosersAndCapsHeavySellStacks()
    {
        var t0 = new DateTimeOffset(2026, 11, 20, 8, 0, 0, TimeSpan.Zero); // Global Off-Peak (08:00 UTC)
        var ledger = new[]
        {
            // Fast winner (Item 301): bought 10s (1000c) x2, sold 16s (1600c, net 1360c) in 2 hours -> +720c profit!
            new Gw2LedgerTransaction(1, TransactionType.Buy, 301, "Fast Winner Material", 1_000, 2, t0.AddHours(-10), t0.AddHours(-9)),
            new Gw2LedgerTransaction(2, TransactionType.Sell, 301, "Fast Winner Material", 1_600, 2, t0.AddHours(-8), t0.AddHours(-7)),

            // Chronic loser (Item 302): bought 20s (2000c) x2, sold 15s (1500c, net 1275c) -> -1450c net loss!
            new Gw2LedgerTransaction(3, TransactionType.Buy, 302, "Chronic Loser Skin", 2_000, 2, t0.AddHours(-20), t0.AddHours(-19)),
            new Gw2LedgerTransaction(4, TransactionType.Sell, 302, "Chronic Loser Skin", 1_500, 2, t0.AddHours(-15), t0.AddHours(-14))
        };

        var activeOrders = new[]
        {
            new Transaction(TransactionType.Sell, "Overexposed Helmet", 25_000, 3, t0.AddHours(-3), 24_000, 15_000)
        };

        var guidance = PortfolioProfitAdvisor.BuildGuidance(ledger, activeOrders, t0, defaultOrderQuantity: 1);
        Assert.True(guidance.IsOffPeak);

        var candidates = new[]
        {
            new ItemCandidate("Unplayed Baseline Flip", Quantity: 1, SellPriceCopper: 1_600, BuyPriceCopper: 1_000, ItemId: 300),
            new ItemCandidate("Fast Winner Material", Quantity: 1, SellPriceCopper: 1_600, BuyPriceCopper: 1_000, ItemId: 301),
            new ItemCandidate("Chronic Loser Skin", Quantity: 1, SellPriceCopper: 2_000, BuyPriceCopper: 1_000, ItemId: 302),
            new ItemCandidate("Overexposed Helmet", Quantity: 1, SellPriceCopper: 25_000, BuyPriceCopper: 15_000, ItemId: 303)
        };

        // Equal live spreads & depths between 300 and 301 so only Portfolio boost distinguishes them
        var quotes = new Dictionary<int, Gw2LivePriceQuote>
        {
            [300] = new(300, HighestBuyCopper: 1_000, BuyQuantity: 500, LowestSellCopper: 1_600, SellQuantity: 500),
            [301] = new(301, HighestBuyCopper: 1_000, BuyQuantity: 500, LowestSellCopper: 1_600, SellQuantity: 500),
            [302] = new(302, HighestBuyCopper: 1_000, BuyQuantity: 500, LowestSellCopper: 2_000, SellQuantity: 500),
            [303] = new(303, HighestBuyCopper: 15_000, BuyQuantity: 500, LowestSellCopper: 25_000, SellQuantity: 500)
        };

        var result = new LiveCandidateProfitRefresher().Refresh(
            candidates,
            quotes,
            new ProfitabilityPolicy(MinimumProfitPercentage: 10m),
            overbidCopper: 1,
            undercutCopper: 1,
            portfolioGuidance: guidance);

        Assert.Equal(2, result.PortfolioSkippedCount);
        Assert.Equal(1, result.PortfolioBoostedCount);
        Assert.Equal(2, result.Candidates.Count);
        Assert.Equal("Fast Winner Material", result.Candidates[0].ItemName);
        Assert.DoesNotContain(result.Candidates, c => c.ItemName is "Chronic Loser Skin" or "Overexposed Helmet");

        var report = TradingIncomeAnalytics.Analyze(ledger);
        var insight = PortfolioProfitAdvisor.BuildDashboardInsightSummary(
            report,
            [new Gw2ActiveOrder(99, TransactionType.Sell, 303, "Overexposed Helmet", 25_000, 3, t0, 15_000, 24_000, "Undercut")],
            t0);
        Assert.Contains("Fast Winner Material", insight);
        Assert.Contains("Skipped for losing money: 1", insight);
    }
}
