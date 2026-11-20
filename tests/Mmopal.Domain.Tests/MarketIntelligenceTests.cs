using System.Text.Json;
using Mmopal.Contracts;
using Mmopal.Gw2;
using Mmopal.Worker;

namespace Mmopal.Domain.Tests;

/// <summary>Trade-history guidance, auto re-scan bookkeeping, and saved scanner filters.</summary>
public sealed class MarketIntelligenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    private static Gw2LedgerTransaction Buy(long id, int itemId, string name, long price, long qty, DateTimeOffset at) =>
        new(id, TransactionType.Buy, itemId, name, price, qty, at, at);

    private static Gw2LedgerTransaction Sell(long id, int itemId, string name, long price, long qty, DateTimeOffset at) =>
        new(id, TransactionType.Sell, itemId, name, price, qty, at, at);

    [Fact]
    public void One_losing_flip_is_not_enough_to_skip_an_item()
    {
        var ledger = new[]
        {
            Buy(1, 401, "Unlucky Once", 2_000, 1, Now.AddDays(-2)),
            Sell(2, 401, "Unlucky Once", 1_500, 1, Now.AddDays(-1))
        };

        var guidance = PortfolioProfitAdvisor.BuildGuidance(ledger, [], Now);

        Assert.Null(guidance.TryGetAdvice(401, "Unlucky Once"));
        Assert.Empty(guidance.LosingItems!);
    }

    [Fact]
    public void Losses_count_only_within_the_last_30_days()
    {
        var ledger = new[]
        {
            Buy(1, 402, "Old Loser", 2_000, 2, Now.AddDays(-60)),
            Sell(2, 402, "Old Loser", 1_500, 2, Now.AddDays(-59)),
            Buy(3, 403, "Recent Loser", 2_000, 2, Now.AddDays(-5)),
            Sell(4, 403, "Recent Loser", 1_500, 2, Now.AddDays(-4))
        };

        var guidance = PortfolioProfitAdvisor.BuildGuidance(ledger, [], Now);

        Assert.Null(guidance.TryGetAdvice(402, "Old Loser"));
        var advice = guidance.TryGetAdvice(403, "Recent Loser");
        Assert.NotNull(advice);
        Assert.True(advice.ShouldSkip);
        Assert.Equal("Skipped: lost money", advice.ShortLabel);
        Assert.Equal(["Recent Loser"], guidance.LosingItems);
    }

    [Fact]
    public void Advice_short_labels_read_well_in_the_scanner()
    {
        Assert.Equal("Boost ×1.4", new PortfolioItemAdvice(1.40, false, "Fast proven FIFO winner").ShortLabel);
        Assert.Equal("Slow ×0.55", new PortfolioItemAdvice(0.55, false, "Slow turnover").ShortLabel);
        Assert.Equal("Skipped: stack listed", new PortfolioItemAdvice(0, true, "Already holding unsold sell stack on TP").ShortLabel);
    }

    [Fact]
    public void Rescan_keeps_the_custom_shopping_list()
    {
        var custom = new[] { new ItemCandidate("Mystic Coin", Quantity: 1) };
        var scanned = new[] { new ItemCandidate("mystic coin", Quantity: 3), new ItemCandidate("Glob of Ectoplasm", Quantity: 1) };

        var merged = WorkerRuntime.MergeWithCustomList(custom, scanned);

        Assert.Equal(["Mystic Coin", "Glob of Ectoplasm"], merged.Select(c => c.ItemName));
        Assert.Same(scanned, WorkerRuntime.MergeWithCustomList([], scanned));
    }

    [Fact]
    public void A_stale_scanned_list_is_rescanned_on_the_first_cycle()
    {
        var path = Path.Combine(Path.GetTempPath(), "mmopal-rescan-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, "[]");
        try
        {
            var written = Now.AddDays(-1);
            File.SetLastWriteTimeUtc(path, written.UtcDateTime);
            var request = new TradingReplayRequest(CandidatesPath: path, AutoRescanFilter: new MarketScanFilter(), AutoRescanIntervalMinutes: 180);

            Assert.Equal(written, WorkerRuntime.InitialRescanClock(request, Now));
            Assert.Equal(Now, WorkerRuntime.InitialRescanClock(request with { AutoRescanFilter = null }, Now));
            Assert.Equal("40 items (35 scanned) · re-scan now", WorkerRuntime.DescribeMarketList(request, 40, 35, written, Now));
            Assert.Equal("40 items (35 scanned) · re-scan in 1h 30m", WorkerRuntime.DescribeMarketList(request, 40, 35, Now.AddMinutes(-90), Now));
            Assert.Equal("12 items · re-scan off (list is not the scanner's)", WorkerRuntime.DescribeMarketList(request with { AutoRescanFilter = null }, 12, 12, Now, Now));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static TradingIncomeReport Results(long unitsSold, decimal roi, double winRate, double holdHours) =>
        new(MatchedProfitCopper: (long)(roi * 1_000), MatchedCostBasisCopper: 100_000, MatchedNetRevenueCopper: 100_000 + (long)(roi * 1_000),
            MatchedRoiPercent: roi, UnmatchedSellRevenueCopper: 0, TotalBuysSpentCopper: 100_000, MatchedUnitsSold: unitsSold,
            UnmatchedUnitsSold: 0, WinRatePercent: winRate, AvgProfitPerActiveDayCopper: 0, DailySeries: [], HourlySeries: [],
            ItemLeaderboard: [new ItemPnlRow(1, "Any", unitsSold, 1_000, 1_200, 100, roi, holdHours, "Win")]);

    [Fact]
    public void AutoTune_sizes_from_total_gold_not_just_the_wallet()
    {
        // 80g wallet alone is Starter; with 400g in orders the account is a Balanced-size trader.
        var walletOnly = SmartAutoTuner.Tune(new AutoTuneInputs(800_000));
        var withOrders = SmartAutoTuner.Tune(new AutoTuneInputs(800_000, ActiveBuysCopper: 2_000_000, ActiveSellsNetCopper: 2_000_000));

        Assert.StartsWith("Starter", walletOnly.Preset.Name);
        Assert.StartsWith("Balanced", withOrders.Preset.Name);
        Assert.Equal(SmartAutoTuner.BaseMinTradesPerDay, walletOnly.MinTradesPerDay);
        Assert.Contains(walletOnly.Adjustments, a => a.StartsWith("Not enough recent flips", StringComparison.Ordinal));
    }

    [Fact]
    public void AutoTune_learns_from_thin_margins_and_slow_sales()
    {
        var baseline = SmartAutoTuner.Tune(new AutoTuneInputs(5_000_000));
        var struggling = SmartAutoTuner.Tune(new AutoTuneInputs(5_000_000, RecentResults: Results(unitsSold: 30, roi: 3m, winRate: 40, holdHours: 72)));

        Assert.Equal(baseline.Preset.MinRoiPercent + 5m, struggling.Preset.MinRoiPercent); // +3 thin margin, +2 low win rate
        Assert.Equal(30, struggling.MinTradesPerDay);                                       // 10 x 1.5 (losers) x 2 (slow)
        Assert.True(struggling.Preset.MaxRoiPercent <= 60m || struggling.Preset.MaxRoiPercent >= struggling.Preset.MinRoiPercent + 20m);
    }

    [Fact]
    public void AutoTune_slows_buying_while_gold_is_stuck_in_listings_and_scales_up_when_idle()
    {
        var stuck = SmartAutoTuner.Tune(new AutoTuneInputs(1_000_000, ActiveSellsNetCopper: 4_000_000));
        var plain = SmartAutoTuner.Tune(new AutoTuneInputs(5_000_000));
        Assert.True(stuck.Preset.MaxBuyGold < plain.Preset.MaxBuyGold);
        Assert.Equal(15, stuck.MinTradesPerDay);

        var idle = SmartAutoTuner.Tune(new AutoTuneInputs(5_000_000, RecentResults: Results(unitsSold: 20, roi: 12m, winRate: 65, holdHours: 10)));
        Assert.Equal((long)Math.Ceiling(plain.Preset.DefaultOrderQuantity * 1.5), idle.Preset.DefaultOrderQuantity);
    }

    [Fact]
    public void Scanner_filters_survive_a_relaunch()
    {
        var settings = new OperatorSettings(Scanner: new ScannerUiState(MinRoi: "15", MaxBuyGold: "5", MinTradesPerDay: "25", Sort: "GoldPerDay", SkipPriceSpikes: false));
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var restored = JsonSerializer.Deserialize<OperatorSettings>(JsonSerializer.Serialize(settings, options), options)!;

        Assert.Equal(settings.Scanner, restored.Scanner);
        Assert.True(restored.AutomateOnLaunch);
        Assert.Null(restored.LastAutoTunedPreset);
    }
}
