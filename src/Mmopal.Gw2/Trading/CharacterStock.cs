using Mmopal.Contracts;

namespace Mmopal.Gw2;

/// <summary>
/// Which character holds unsold flip items. Listing works anywhere, but only from the logged-in character's bags,
/// so stock bought on another character waits until a swap lands on it (21-22 Nov: ~145g across 5 characters,
/// 93g of it on one character the random swaps rarely reached).
/// </summary>
public static class CharacterStock
{
    /// <summary>A character needs at least this many flip items before a swap is made just to list them.</summary>
    public const int MinItemsForStockSwap = 5;

    /// <summary>
    /// Characters holding flip items that can be listed, most items first (name order breaks ties). Stacks that hold
    /// copies of the player's own are left out: on 22-23 Nov swaps kept choosing a character for ~16 units it was not
    /// allowed to list. <paramref name="heldByMmopal"/> null (not read yet) counts everything.
    /// </summary>
    public static IReadOnlyList<(string Name, long Items)> Rank(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, long>> unitsByCharacter,
        IReadOnlyDictionary<string, long>? heldByMmopal) =>
        unitsByCharacter
            .Select(pair => (
                Name: pair.Key,
                Items: pair.Value.Where(item => heldByMmopal is null || OwnedStock.StackIsMmopals(item.Key, item.Value, heldByMmopal)).Sum(item => item.Value)))
            .Where(entry => entry.Items > 0)
            .OrderByDescending(entry => entry.Items)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>
    /// The character worth swapping to only to list its stock: the one holding the most flip items, when that is
    /// not the current character, it holds at least <see cref="MinItemsForStockSwap"/>, and more than the current
    /// one. Null when the current character is unknown (the swap could land back on it) or nothing qualifies.
    /// </summary>
    public static string? StockSwapTarget(IReadOnlyList<(string Name, long Items)> ranked, string? currentCharacter)
    {
        if (string.IsNullOrWhiteSpace(currentCharacter) || ranked.Count == 0)
        {
            return null;
        }

        var current = ranked.FirstOrDefault(entry => NamesMatch(entry.Name, currentCharacter)).Items;
        var best = ranked.FirstOrDefault(entry => !NamesMatch(entry.Name, currentCharacter));
        return best.Name is not null && best.Items >= MinItemsForStockSwap && best.Items > current ? best.Name : null;
    }

    /// <summary>A character holding at least this many flip items lists twice as many per sell pass.</summary>
    public const int BacklogItemsForBiggerSellPass = 10;

    /// <summary>
    /// Unsold units of each flip item across all characters' bags, by item name: at most the units that are Mmopal's, so
    /// the player's own copies do not stop Mmopal from buying the item.
    /// </summary>
    public static IReadOnlyDictionary<string, long> HeldByItem(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, long>> unitsByCharacter,
        IReadOnlyDictionary<string, long>? heldByMmopal) =>
        unitsByCharacter.Values
            .SelectMany(units => units)
            .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var inBags = group.Sum(item => item.Value);
                    return heldByMmopal is null ? inBags : Math.Clamp(heldByMmopal.GetValueOrDefault(ItemNameNormalizer.Normalize(group.Key)), 0, inBags);
                },
                StringComparer.OrdinalIgnoreCase);

    /// <summary>Names of the flip items each character holds, by character.</summary>
    public static IReadOnlyDictionary<string, IReadOnlySet<string>> ItemsByCharacter(
        IReadOnlyDictionary<string, IReadOnlyDictionary<int, long>> bagsByCharacter,
        IReadOnlyDictionary<int, string> flipItemNames) =>
        bagsByCharacter.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlySet<string>)pair.Value.Keys
                .Where(flipItemNames.ContainsKey)
                .Select(id => flipItemNames[id])
                .ToHashSet(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Sell targets the logged-in character can list: drops items the API shows only on other characters. Kept: items
    /// on this character, items no bag shows yet (just collected from the Delivery Box), and everything when the
    /// current character is unknown. In 2.3 h on 22 Nov, 74 bag searches missed items held by other characters.
    /// </summary>
    public static IReadOnlyList<string> ListableHere(
        IEnumerable<string> targets,
        IReadOnlyDictionary<string, IReadOnlySet<string>> itemsByCharacter,
        string? currentCharacter)
    {
        var here = itemsByCharacter.FirstOrDefault(pair => NamesMatch(pair.Key, currentCharacter)).Value;
        if (here is null)
        {
            return targets.ToArray();
        }

        return targets
            .Where(item => here.Contains(item) || !itemsByCharacter.Values.Any(items => items.Contains(item)))
            .ToArray();
    }

    /// <summary>
    /// The only character whose bags held <paramref name="itemName"/>, or null when none or several did. Listing an item
    /// from the bags shows which character is logged in when Character Select could not be read.
    /// </summary>
    public static string? OnlyHolder(string itemName, IReadOnlyDictionary<string, IReadOnlySet<string>> itemsByCharacter)
    {
        var holders = itemsByCharacter.Where(pair => pair.Value.Contains(itemName)).Select(pair => pair.Key).Take(2).ToArray();
        return holders.Length == 1 ? holders[0] : null;
    }

    /// <summary>
    /// Candidates minus items that already hold at least one order's worth of unsold units in bags. Buying more of
    /// an item that has not been listed yet only ties up gold: on 21-22 Nov 97 bought items (~145g) sat unsold while
    /// the same items kept being re-bought (Twin Sisters: 11 orders placed in the run, 8 still unsold in bags at the end).
    /// </summary>
    public static IReadOnlyList<ItemCandidate> WithoutHeldStock(
        IReadOnlyList<ItemCandidate> candidates,
        IReadOnlyDictionary<string, long> heldByItem,
        long orderQuantity) =>
        heldByItem.Count == 0
            ? candidates
            : candidates
                .Where(candidate => heldByItem.GetValueOrDefault(candidate.ItemName.Trim()) < Math.Max(1, candidate.Quantity ?? orderQuantity))
                .ToArray();

    /// <summary>
    /// True when an OCR read of the Character Select name and an API character name are the same character. Only
    /// letters and digits are compared, and a read may lose a few edge characters ("Mesmerising Rizz").
    /// </summary>
    public static bool NamesMatch(string? a, string? b)
    {
        var left = Normalize(a);
        var right = Normalize(b);
        if (left.Length == 0 || right.Length == 0)
        {
            return false;
        }

        if (left == right)
        {
            return true;
        }

        var (shorter, longer) = left.Length <= right.Length ? (left, right) : (right, left);
        return shorter.Length >= 6 && longer.Length - shorter.Length <= 3 && longer.Contains(shorter, StringComparison.Ordinal);
    }

    private static string Normalize(string? name) =>
        new((name ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
