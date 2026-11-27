using Mmopal.Contracts;
using Mmopal.Gw2;
using Mmopal.Worker;

namespace Mmopal.Domain.Tests;

public sealed class GoldGoalAndMemoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 2, 0, 0, TimeSpan.Zero);

    private static Transaction Buy(string item, long price, int quantity, double hoursAgo, long? topBid = null) =>
        new(TransactionType.Buy, item, price, quantity, Now.AddHours(-hoursAgo), null, topBid);

    [Fact]
    public void Kept_gold_raises_the_floor_and_free_gold_is_a_floor_of_its_own()
    {
        Assert.Equal(1_770_000, new GoldGoal(KeepCopper: 1_000_000).Floor(770_000));         // 77g reserve + 100g kept
        Assert.Equal(5_000_000, new GoldGoal(FreeCopper: 5_000_000).Floor(770_000));         // 500g free
        Assert.Equal(770_000, new GoldGoal().Floor(770_000));
        Assert.Equal(1_880_000, new GoldGoal(FreeCopper: 5_000_000).Shortfall(3_120_000));
    }

    [Fact]
    public void Free_gold_comes_from_outbid_orders_first_then_the_oldest_and_never_the_players()
    {
        Transaction[] orders =
        [
            Buy("Old Top Bid", 500_000, 2, 10),
            Buy("Outbid", 300_000, 2, 1, topBid: 300_001),
            Buy("Young Top Bid", 400_000, 2, 0.5),
            Buy("Players Own", 900_000, 2, 20),
        ];

        var cancels = new GoldGoal(FreeCopper: 5_000_000).OrdersToCancel(orders, 3_500_000, order => order.ItemName != "Players Own");

        // 150g missing: the outbid order (60g), then the oldest (100g); the player's own order stays.
        Assert.Equal(["Outbid", "Old Top Bid"], cancels.Select(order => order.ItemName));
    }

    [Fact]
    public void Free_gold_cancels_as_many_orders_as_the_shortfall_needs()
    {
        Transaction[] orders = [Buy("Old Top Bid", 500_000, 2, 10), Buy("Outbid", 300_000, 2, 1, topBid: 300_001), Buy("Young Top Bid", 400_000, 2, 0.5)];

        Assert.Equal(["Outbid"], new GoldGoal(FreeCopper: 5_000_000).OrdersToCancel(orders, 4_500_000, _ => true).Select(order => order.ItemName));
        Assert.Equal(3, new GoldGoal(FreeCopper: 5_000_000).OrdersToCancel(orders, 3_000_000, _ => true).Count);
        Assert.Empty(new GoldGoal(FreeCopper: 5_000_000).OrdersToCancel(orders, 5_000_000, _ => true));
    }

    [Fact]
    public void The_goal_says_when_it_cannot_be_reached_without_a_loss()
    {
        var goal = new GoldGoal(FreeCopper: 10_000_000, FreeByUtc: Now.AddHours(2));

        Assert.Contains("Even with every order cancelled", goal.Describe(3_000_000, 2_000_000, 1_000_000, Now));
        Assert.Contains("more from sales", goal.Describe(3_000_000, 2_000_000, 6_000_000, Now));
        Assert.Contains("free of the", goal.Describe(10_500_000, 0, 0, Now));
        Assert.Null(new GoldGoal(KeepCopper: 1).Describe(1, 1, 1, Now));
    }

    [Fact]
    public void Orders_that_must_go_for_free_gold_are_cancelled_young_and_at_the_top()
    {
        Transaction[] orders = [Buy("Young Top Bid", 400_000, 2, 0.1), Buy("Kept", 300_000, 2, 0.1)];

        var picks = Gw2TradingPostUiDriver.SelectSafeOutbidCancels(orders, 5, Now, out _, out _, mustGo: order => order.ItemName == "Young Top Bid");

        Assert.Equal("Young Top Bid", Assert.Single(picks).ItemName);
    }

    private static Gw2LedgerTransaction Row(long id, TransactionType kind, string item, long price, long quantity, DateTimeOffset created, DateTimeOffset at) =>
        new(id, kind, 1, item, price, quantity, created, at);

    [Fact]
    public void Items_are_worked_by_their_expected_return_per_gold()
    {
        var placed = new List<PlacedBuyOrder>();
        var history = new List<Gw2LedgerTransaction>();
        long id = 0;
        void Trade(string item, int orders, int filledPerOrder, long buy, long sell)
        {
            for (var i = 0; i < orders; i++)
            {
                var at = Now.AddHours(-48 + i);
                placed.Add(new PlacedBuyOrder(item, at, buy, 2));
                if (filledPerOrder == 0) continue;
                history.Add(Row(++id, TransactionType.Buy, item, buy, filledPerOrder, at.AddSeconds(3), at.AddHours(1)));
                history.Add(Row(++id, TransactionType.Sell, item, sell, filledPerOrder, at.AddHours(1), at.AddHours(2)));
            }
        }

        Trade("Arms of the People", 10, 2, 2_500, 4_600);      // fills, 56% margin
        Trade("Vintage Black Lion Weapon Box", 6, 1, 372_000, 477_000); // half fills, 9% margin
        Trade("Djinn Energy Cluster", 10, 0, 50_000, 0);       // never fills
        var memory = ItemMemory.Build(placed, history, Now);

        ItemCandidate[] list =
        [
            new("Djinn Energy Cluster", Roi: 40m),
            new("Vintage Black Lion Weapon Box", Roi: 25m),
            new("Never Traded", Roi: 20m),
            new("Arms of the People", Roi: 30m),
        ];

        Assert.Equal(["Arms of the People", "Never Traded", "Vintage Black Lion Weapon Box", "Djinn Energy Cluster"], memory.Rank(list).Select(c => c.ItemName));
        Assert.Equal(1.0, memory["Arms of the People"]!.UnitsFilled / (double)memory["Arms of the People"]!.UnitsOrdered);
    }
}
