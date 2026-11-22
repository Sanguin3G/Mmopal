using Mmopal.Contracts;
using Mmopal.Gw2;
using Mmopal.Worker;

namespace Mmopal.Domain.Tests;

/// <summary>Rules learned from the 7 h session of 2026-11-20: bot wars, gold stuck in orders, stale listings, wasted waits.</summary>
public sealed class AdaptiveTradingTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Bot_war_items_pause_after_three_outbid_cancels_without_a_fill()
    {
        var tracker = new BuyCompetitionTracker();

        Assert.Empty(tracker.RecordOutbidCancels(["Super Sharpening Polygon"], Now));
        Assert.Empty(tracker.RecordOutbidCancels(["Super Sharpening Polygon", "Flare Dye"], Now));
        Assert.Equal(["Super Sharpening Polygon"], tracker.RecordOutbidCancels(["super sharpening polygon"], Now));

        Assert.True(tracker.IsPaused("Super Sharpening Polygon", Now.AddHours(2)));
        Assert.False(tracker.IsPaused("Super Sharpening Polygon", Now.AddHours(3.1)));
        Assert.False(tracker.IsPaused("Flare Dye", Now));

        // A fill clears both the count and the pause.
        tracker.RecordFills(["Super Sharpening Polygon", "Flare Dye"]);
        Assert.False(tracker.IsPaused("Super Sharpening Polygon", Now));
        Assert.Empty(tracker.RecordOutbidCancels(["Flare Dye"], Now));
    }

    [Fact]
    public void Buy_orders_are_capped_at_a_share_of_total_gold()
    {
        // 66g wallet, 320g in buys, ~147g listings after fees: the 7 h session's end state.
        var market = new MarketSnapshot(
            [
                new Transaction(TransactionType.Buy, "Lots of buys", 1_600_000, 2, Now, null, null),
                new Transaction(TransactionType.Sell, "Listings", 1_730_000, 1, Now, null, null)
            ],
            new AccountBalance(660_000),
            Now,
            "test");

        var budget = TradingCycleController.BuyOrderBudget(market, 50);

        Assert.NotNull(budget);
        Assert.Equal(0, budget.Value.Remaining); // 3,200,000 already exceeds half of ~5.33M
        Assert.Equal(3_200_000, budget.Value.InBuyOrders);
        Assert.Null(TradingCycleController.BuyOrderBudget(market, 0));
        Assert.True(TradingCycleController.BuyOrderBudget(market, 70)!.Value.Remaining > 0);
    }

    private static Transaction Listing(string name, long price, long lowestAsk, double hoursAgo) =>
        new(TransactionType.Sell, name, price, 1, Now.AddHours(-hoursAgo), lowestAsk, null);

    [Fact]
    public void Relist_skips_items_with_several_of_our_listings()
    {
        // Pyre Dye, 22 Nov: three listings; the cancel flow clicks the first Cancel button its search shows, which
        // was not the stale one, twice.
        var orders = new[]
        {
            Listing("Pyre Dye", 13_988, 9_208, 43),
            Listing("Pyre Dye", 12_040, 9_208, 38),
            Listing("Pyre Dye", 9_208, 9_208, 1),
            Listing("Ebonblade", 3_912, 3_686, 36),
        };
        var paid = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase) { ["Pyre Dye"] = 6_996, ["Ebonblade"] = 2_036 };
        var traded = new HashSet<string>(["Pyre Dye", "Ebonblade"], StringComparer.OrdinalIgnoreCase);

        var picks = SellRelistPolicy.Select(orders, paid, traded, new Dictionary<string, DateTimeOffset>(), undercutCopper: 1, Now, maxPerPass: 5);

        Assert.Equal("Ebonblade", Assert.Single(picks).Listing.ItemName);
    }

    [Fact]
    public void Relist_only_old_undercut_traded_items_that_still_cover_cost_and_the_lost_fee()
    {
        var orders = new[]
        {
            Listing("Old Undercut", 13_000, 12_500, 20),     // 12,499 nets 10,624 >= 9,000 paid + 650 lost fee
            Listing("Young Undercut", 13_000, 12_500, 3),    // too young
            Listing("Thin Margin", 13_000, 11_000, 20),      // 10,999 nets 9,349 < 9,000 + 650
            Listing("Not Traded", 13_000, 12_500, 20),       // not on the scanner's list
            Listing("At The Top", 13_000, 13_000, 20),       // we are the lowest ask
            Listing("Relisted Today", 13_000, 12_500, 20)
        };
        var paid = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        {
            ["Old Undercut"] = 9_000, ["Young Undercut"] = 9_000, ["Thin Margin"] = 9_000,
            ["Not Traded"] = 9_000, ["At The Top"] = 9_000, ["Relisted Today"] = 9_000
        };
        var traded = new HashSet<string>(["Old Undercut", "Young Undercut", "Thin Margin", "At The Top", "Relisted Today"], StringComparer.OrdinalIgnoreCase);
        var relisted = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase) { ["Relisted Today"] = Now.AddHours(-5) };

        var picks = SellRelistPolicy.Select(orders, paid, traded, relisted, undercutCopper: 1, Now, maxPerPass: 5);

        var pick = Assert.Single(picks);
        Assert.Equal("Old Undercut", pick.Listing.ItemName);
        Assert.Equal(12_499, pick.NewPriceCopper);
    }

    [Fact]
    public void Items_on_another_character_wait_for_a_character_change()
    {
        var tracker = new PendingSellInventoryTracker();
        tracker.RecordNewBuyFill(["Baby Blue Dye"], Now);
        tracker.RecordSellPassResults([new SellPassItemResult("Baby Blue Dye", SellPassItemOutcome.NotFoundInBags)], Now);
        tracker.RecordSellPassResults([new SellPassItemResult("Baby Blue Dye", SellPassItemOutcome.NotFoundInBags)], Now.AddMinutes(6));

        // The account-wide bag sync keeps reporting it, but it stays parked (it used to come back every 15 min).
        tracker.SyncFromCharacterInventory(["Baby Blue Dye"], Now.AddMinutes(30));
        Assert.Empty(tracker.GetReadyTargets(Now.AddMinutes(30), 3));

        tracker.CharacterChanged(Now.AddMinutes(40));
        tracker.SyncFromCharacterInventory(["Baby Blue Dye"], Now.AddMinutes(40));
        Assert.Equal(["Baby Blue Dye"], tracker.GetReadyTargets(Now.AddMinutes(40), 3));
    }

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(0, 1, false)]
    [InlineData(0, 2, true)]   // never confirmed twice: wait 2 s, not 7.5 s
    [InlineData(1, 2, false)]  // as often seen as missed: keep the full wait
    [InlineData(1, 5, true)]   // read once in six searches: a short wait still catches the odd confirmation
    [InlineData(4, 6, false)]
    public void Search_wait_shortens_for_names_that_mostly_never_confirm(int seen, int missed, bool expected)
    {
        Assert.Equal(expected, Gw2TradingPostUiDriver.UsesShortSearchWait(seen, missed));
    }

    private static Gw2LedgerTransaction Tx(long id, TransactionType kind, int itemId, long price, long qty, double hoursAgo) =>
        new(id, kind, itemId, $"Item {itemId}", price, qty, Now.AddHours(-hoursAgo), Now.AddHours(-hoursAgo));

    [Fact]
    public void Unsold_cost_and_fees_follow_first_in_first_out()
    {
        var history = new[]
        {
            Tx(1, TransactionType.Buy, 1, 1_000, 3, 10),
            Tx(2, TransactionType.Buy, 1, 1_200, 2, 9),
            Tx(3, TransactionType.Sell, 1, 2_000, 4, 5),  // uses 3 x 1,000 and 1 x 1,200
            Tx(4, TransactionType.Buy, 2, 500, 1, 2)
        };

        Assert.Equal(1_200 + 500, IncomeInsights.UnsoldCost(history));
        Assert.Equal((100 + 200) * 4, IncomeInsights.FeesPaid(history, null));
        Assert.Equal(0, IncomeInsights.FeesPaid(history, Now.AddHours(-1)));
    }

    [Fact]
    public void Net_worth_change_and_series_use_the_snapshots_in_the_window()
    {
        static Gw2EquitySnapshot Snap(DateTimeOffset at, long total) => new(at, 0, 0, 0, 0, 0, total, 0, 0);
        var snapshots = Enumerable.Range(0, 40)
            .Select(i => Snap(Now.AddHours(-7).AddMinutes(i * 11), 4_764_000 + i * 15_000))
            .ToArray();

        var change = IncomeInsights.NetWorthChange(snapshots, Now.AddDays(-30));
        Assert.NotNull(change);
        Assert.Equal(4_764_000, change.Value.Start.ValueCopper);
        Assert.Equal(4_764_000 + 39 * 15_000, change.Value.End.ValueCopper);

        var series = IncomeInsights.NetWorthSeries(snapshots, null, maxPoints: 24);
        Assert.Equal(24, series.Count);
        Assert.Equal(snapshots[0].TotalEquityCopper, series[0].ValueCopper);
        Assert.Equal(snapshots[^1].TotalEquityCopper, series[^1].ValueCopper);
        Assert.Null(IncomeInsights.NetWorthChange([snapshots[0]], null));
    }

    [Fact]
    public void Shelf_counts_undercut_and_stale_listings()
    {
        var orders = new[]
        {
            new Gw2ActiveOrder(1, TransactionType.Sell, 1, "A", 10_000, 1, Now.AddHours(-20), null, 9_000, "Undercut"),
            new Gw2ActiveOrder(2, TransactionType.Sell, 2, "B", 10_000, 2, Now.AddHours(-2), null, 9_500, "Undercut"),
            new Gw2ActiveOrder(3, TransactionType.Sell, 3, "C", 10_000, 1, Now.AddHours(-30), null, 10_000, "Top"),
            new Gw2ActiveOrder(4, TransactionType.Buy, 4, "D", 5_000, 1, Now, 5_000, null, "Top")
        };

        var shelf = IncomeInsights.Shelf(orders, [], Now);

        Assert.Equal(3, shelf.Listings);
        Assert.Equal(2, shelf.Undercut);
        Assert.Equal(1, shelf.UndercutOver12h);
        Assert.Equal(8_500 * 4, shelf.ListedNetCopper);
    }
}
