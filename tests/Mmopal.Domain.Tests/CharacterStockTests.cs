using Mmopal.Contracts;
using Mmopal.Gw2;

namespace Mmopal.Domain.Tests;

public sealed class CharacterStockTests
{
    private static readonly Dictionary<string, IReadOnlyDictionary<int, long>> Bags = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Mesmerising Rizzo"] = new Dictionary<int, long> { [1] = 8, [2] = 1, [99] = 500 },
        ["Johanne Shepard"] = new Dictionary<int, long> { [1] = 2 },
    };

    [Fact]
    public void HeldByItem_adds_up_flip_items_across_characters_and_ignores_the_rest()
    {
        var held = CharacterStock.HeldByItem(Bags, new Dictionary<int, string> { [1] = "Twin Sisters", [2] = "Pyre Dye" });

        Assert.Equal(10, held["twin sisters"]);
        Assert.Equal(1, held["Pyre Dye"]);
        Assert.Equal(2, held.Count); // item 99 is not a flip item
    }

    [Fact]
    public void WithoutHeldStock_skips_items_holding_an_order_worth_of_unsold_units()
    {
        var held = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase) { ["Twin Sisters"] = 10, ["Pyre Dye"] = 1 };
        ItemCandidate[] candidates =
        [
            new("Twin Sisters"),
            new("Pyre Dye", Quantity: 3), // 1 unsold < 3 per order: still bought
            new("Abyss Dye"),
        ];

        var kept = CharacterStock.WithoutHeldStock(candidates, held, orderQuantity: 1);

        Assert.Equal(["Pyre Dye", "Abyss Dye"], kept.Select(c => c.ItemName));
        Assert.Same(candidates, CharacterStock.WithoutHeldStock(candidates, new Dictionary<string, long>(), 1));
    }
}
