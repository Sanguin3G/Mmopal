using Mmopal.Contracts;
using Mmopal.Gw2;

namespace Mmopal.Domain.Tests;

public sealed class OwnedStockTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 22, 10, 0, 0, TimeSpan.Zero);

    private static Gw2LedgerTransaction Buy(long id, string item, long price, long qty, double hours) =>
        new(id, TransactionType.Buy, 1, item, price, qty, T0.AddHours(hours - 1), T0.AddHours(hours));

    // Mmopal's placing finished a few seconds before GW2 recorded the order.
    private static PlacedBuyOrder Placed(string item, double filledAtHours) => new(item, T0.AddHours(filledAtHours - 1).AddSeconds(-4));

    private static Gw2LedgerTransaction Sell(long id, string item, long price, long qty, double hours) =>
        new(id, TransactionType.Sell, 1, item, price, qty, T0.AddHours(hours - 1), T0.AddHours(hours));

    [Fact]
    public void Only_fills_of_orders_created_by_Mmopal_count_minus_sold_and_listed_units()
    {
        PlacedBuyOrder[] placed =
        [
            Placed("Twin Sisters", 1),
            Placed("Plate of Spicy Moa Wings", 1),
        ];
        Gw2LedgerTransaction[] history =
        [
            Buy(1, "Twin Sisters", 4_557, 3, 1),            // Mmopal's fill
            Buy(1, "Twin Sisters", 4_557, 3, 1),            // same row from the newest page: counted once
            Buy(2, "Twin Sisters", 3_900, 5, 2),            // ordered by hand an hour later
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
    public void Only_buy_orders_created_while_Mmopal_placed_one_are_its_own()
    {
        PlacedBuyOrder[] placed = [new("Valkyrie Draconic Coat", T0.AddSeconds(-4))];

        Assert.True(OwnedStock.IsMmopalsOrder(new(TransactionType.Buy, "Valkyrie Draconic Coat", 7_811, 15, T0, null, 7_811), placed));
        Assert.False(OwnedStock.IsMmopalsOrder(new(TransactionType.Buy, "Valkyrie Draconic Coat", 7_811, 1, T0.AddHours(3), null, 7_811), placed)); // by hand, same price
        Assert.False(OwnedStock.IsMmopalsOrder(new(TransactionType.Buy, "Unopened Endless Mystery Tonic", 40_003, 21, T0, null, 60_000), placed));
    }

    [Fact]
    public void An_order_for_the_wrong_item_is_still_Mmopals_to_cancel()
    {
        // 22 Nov: "Big Juju" was meant, "Bolt" was ordered, at the typed price and within a second.
        PlacedBuyOrder[] placed = [new("Big Juju", T0, 4_043)];

        Assert.True(OwnedStock.IsMmopalsOrder(new(TransactionType.Buy, "Bolt", 4_043, 2, T0.AddSeconds(-1), null, 11_993_108), placed));
        Assert.False(OwnedStock.IsMmopalsOrder(new(TransactionType.Buy, "Bolt", 4_050, 2, T0.AddSeconds(-1), null, 11_993_108), placed));
        Assert.False(OwnedStock.IsMmopalsOrder(new(TransactionType.Buy, "Bolt", 4_043, 2, T0.AddSeconds(-60), null, 11_993_108), placed));
    }

    [Fact]
    public void Only_Mmopals_trades_are_kept_for_its_own_figures()
    {
        Gw2LedgerTransaction[] history =
        [
            Buy(1, "Twin Sisters", 4_557, 2, 1),
            Buy(2, "Vial of Powerful Blood", 900, 250, 1),   // the player's
            Sell(3, "Twin Sisters", 6_600, 3, 3),            // one of the three is the player's
            Sell(4, "Vial of Powerful Blood", 1_400, 250, 4),
        ];

        var trades = MmopalTrades.Only(history, [Placed("Twin Sisters", 1)]);

        Assert.Equal([2L, 2L], trades.Select(trade => trade.Quantity));
        Assert.All(trades, trade => Assert.Equal("Twin Sisters", trade.ItemName));
        Assert.Equal(0, IncomeInsights.UnsoldCost(trades));
    }

    [Fact]
    public void A_listing_cancelled_by_hand_returns_to_Mmopals_count()
    {
        PlacedBuyOrder[] placed = [Placed("Ebonblade", 1)];
        Gw2LedgerTransaction[] history = [Buy(1, "Ebonblade", 2_036, 2, 1)];

        var whileListed = OwnedStock.UnitsHeldByMmopal(placed, history, [new(TransactionType.Sell, "Ebonblade", 3_912, 2, T0.AddHours(2), null, null)]);
        var afterCancel = OwnedStock.UnitsHeldByMmopal(placed, history, []);

        Assert.Equal(0, whileListed[ItemNameNormalizer.Normalize("Ebonblade")]);
        Assert.True(OwnedStock.StackIsMmopals("Ebonblade", 2, afterCancel));
        Assert.True(OwnedStock.ListingsAreMmopals("Ebonblade", whileListed));

        // A third one listed by the player: none of the listings may be cancelled for relisting.
        var withOwn = OwnedStock.UnitsHeldByMmopal(placed, history, [new(TransactionType.Sell, "Ebonblade", 3_912, 3, T0.AddHours(2), null, null)]);
        Assert.False(OwnedStock.ListingsAreMmopals("Ebonblade", withOwn));
        Assert.False(OwnedStock.ListingsAreMmopals("Holographic Super Cheese", withOwn));
    }
}
