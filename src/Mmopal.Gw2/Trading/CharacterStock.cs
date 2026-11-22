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
