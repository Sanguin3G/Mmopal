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

    /// <summary>Characters holding flip items, most items first (name order breaks ties).</summary>
    public static IReadOnlyList<(string Name, long Items)> Rank(
        IReadOnlyDictionary<string, IReadOnlyDictionary<int, long>> bagsByCharacter,
        IReadOnlySet<int> flipItemIds) =>
        bagsByCharacter
            .Select(pair => (Name: pair.Key, Items: pair.Value.Where(item => flipItemIds.Contains(item.Key)).Sum(item => item.Value)))
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

    /// <summary>Unsold units of each flip item across all characters' bags, by item name.</summary>
    public static IReadOnlyDictionary<string, long> HeldByItem(
        IReadOnlyDictionary<string, IReadOnlyDictionary<int, long>> bagsByCharacter,
        IReadOnlyDictionary<int, string> flipItemNames)
    {
        var held = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var (itemId, count) in bagsByCharacter.Values.SelectMany(bag => bag))
        {
            if (flipItemNames.TryGetValue(itemId, out var name))
            {
                held[name] = held.GetValueOrDefault(name) + count;
            }
        }

        return held;
    }

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
