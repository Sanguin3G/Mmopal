using Mmopal.Contracts;
using Mmopal.Gw2;

namespace Mmopal.Domain.Tests;

public sealed class OwnedStockTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 22, 10, 0, 0, TimeSpan.Zero);

    private static Gw2LedgerTransaction Buy(long id, string item, long price, long qty, double hours) =>
        new(id, TransactionType.Buy, 1, item, price, qty, T0.AddHours(hours - 1), T0.AddHours(hours));

    private static Gw2LedgerTransaction Sell(long id, string item, long price, long qty, double hours) =>
        new(id, TransactionType.Sell, 1, item, price, qty, T0.AddHours(hours - 1), T0.AddHours(hours));

    [Fact]
    public void Only_fills_at_Mmopals_order_prices_count_minus_sold_and_listed_units()
    {
        PlacedBuyOrder[] placed =
        [
            new("Twin Sisters", 4_500, 4_557, T0),          // typed 45s 57c
            new("Plate of Spicy Moa Wings", 5_135, null, T0),
        ];
        Gw2LedgerTransaction[] history =
        [
            Buy(1, "Twin Sisters", 4_557, 3, 1),            // Mmopal's fill
            Buy(1, "Twin Sisters", 4_557, 3, 1),            // same row from the newest page: counted once
            Buy(2, "Twin Sisters", 3_900, 5, 2),            // bought by hand at another price
            Sell(3, "Twin Sisters", 6_600, 1, 3),
            Buy(4, "Plate of Spicy Moa Wings", 5_135, 1, 1),
            Buy(5, "+8 Agony Infusion", 25_000, 1, 1),      // never ordered by Mmopal
        ];
        Transaction[] current = [new(TransactionType.Sell, "Twin Sisters", 6_650, 1, T0.AddHours(4), null, null)];

        var held = OwnedStock.UnitsHeldByMmopal(placed, history, current);

        Assert.Equal(1, held[ItemNameNormalizer.Normalize("Twin Sisters")]);      // 3 bought - 1 sold - 1 listed
        Assert.True(OwnedStock.StackIsMmopals("Plate of Spicy Moa Wings", 1, held));
        Assert.False(OwnedStock.StackIsMmopals("Twin Sisters", 6, held));          // 5 of those are the player's
        Assert.False(OwnedStock.StackIsMmopals("+8 Agony Infusion", 1, held));
    }

    [Fact]
    public void Only_buy_orders_at_Mmopals_prices_are_its_own()
    {
        PlacedBuyOrder[] placed = [new("Valkyrie Draconic Coat", 7_811, null, T0)];

        Assert.True(OwnedStock.IsMmopalsOrder(new(TransactionType.Buy, "Valkyrie Draconic Coat", 7_811, 15, T0, null, 7_811), placed));
        Assert.False(OwnedStock.IsMmopalsOrder(new(TransactionType.Buy, "Unopened Endless Mystery Tonic", 40_003, 21, T0, null, 60_000), placed));
    }

    [Fact]
    public void A_listing_cancelled_by_hand_returns_to_Mmopals_count()
    {
        PlacedBuyOrder[] placed = [new("Ebonblade", 2_036, null, T0)];
        Gw2LedgerTransaction[] history = [Buy(1, "Ebonblade", 2_036, 2, 1)];

        var whileListed = OwnedStock.UnitsHeldByMmopal(placed, history, [new(TransactionType.Sell, "Ebonblade", 3_912, 2, T0.AddHours(2), null, null)]);
        var afterCancel = OwnedStock.UnitsHeldByMmopal(placed, history, []);

        Assert.Equal(0, whileListed[ItemNameNormalizer.Normalize("Ebonblade")]);
        Assert.True(OwnedStock.StackIsMmopals("Ebonblade", 2, afterCancel));
    }
}
