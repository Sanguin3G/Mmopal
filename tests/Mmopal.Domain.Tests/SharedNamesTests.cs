using Mmopal.Gw2;

namespace Mmopal.Domain.Tests;

public sealed class SharedNamesTests
{
    [Fact]
    public void Names_carried_by_several_tradable_items_are_shared()
    {
        MarketItemStats[] stats =
        [
            new(10, null, null, "Berserker's Ring"),
            new(10, null, null, "berserker's ring"), // another rarity/level
            new(10, null, null, "Pyre Dye"),
            new(10, null, null, null),
        ];

        var shared = Gw2MarketScanner.SharedNames(stats);

        Assert.Contains(ItemNameNormalizer.Normalize("Berserker's Ring"), shared);
        Assert.DoesNotContain(ItemNameNormalizer.Normalize("Pyre Dye"), shared);
    }
}
