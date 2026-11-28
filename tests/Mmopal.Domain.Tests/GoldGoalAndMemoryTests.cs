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
    public void Kept_gold_is_added_to_the_floor()
    {
        Assert.Equal(1_770_000, new GoldGoal(KeepCopper: 1_000_000).Floor(770_000)); // 77g reserve + 100g kept
        Assert.Equal(770_000, new GoldGoal().Floor(770_000));
        Assert.False(new GoldGoal().IsSet);
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
