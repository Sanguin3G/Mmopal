using Mmopal.Contracts;

namespace Mmopal.Gw2;

/// <summary>A live buy order Mmopal placed: the item and when the placing finished.</summary>
public sealed record PlacedBuyOrder(string ItemName, DateTimeOffset PlacedAt);

/// <summary>
/// How many units of each item Mmopal itself still holds, so the sell pass never lists the player's own copies.
/// The sell dialog lists a whole bag stack, and an item counted as a flip item only because it was on the shopping
/// list (or once ordered by Mmopal) would take personal copies with it: food, infusions, a legendary lying around.
/// An order is Mmopal's when GW2 records it as created while Mmopal was placing one for that item. Over 15-23 Nov all
/// 707 fills matched that way, within 15 s; matching prices missed 26 of them (the typed price was not recorded), which
/// left ~16 of Mmopal's units unsold on one character. Units bought = those fills; minus units sold since the first
/// one; minus units listed now. Listings the player cancels by hand return to the count, so Mmopal keeps trading its
/// own units. A negative count means the player also listed or sold copies of their own.
/// </summary>
public static class OwnedStock
{
    public static IReadOnlyDictionary<string, long> UnitsHeldByMmopal(
        IEnumerable<PlacedBuyOrder> placedOrders,
        IEnumerable<Gw2LedgerTransaction> history,
        IEnumerable<Transaction> currentOrders)
    {
        var placedByItem = placedOrders
            .GroupBy(order => ItemNameNormalizer.Normalize(order.ItemName), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(order => order.PlacedAt).ToArray(), StringComparer.OrdinalIgnoreCase);

        var rows = history.GroupBy(row => (row.Kind, row.Id)).Select(group => group.First()).ToArray();
        var held = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var firstFill = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        foreach (var fill in rows.Where(row => row.Kind == TransactionType.Buy).OrderBy(row => row.PurchasedAt))
        {
            var key = ItemNameNormalizer.Normalize(fill.ItemName);
            if (!placedByItem.TryGetValue(key, out var placedAt) || !placedAt.Any(at => CreatedWith(fill.CreatedAt, at)))
            {
                continue; // a personal purchase (or one Mmopal did not place)
            }

            held[key] = held.GetValueOrDefault(key) + fill.Quantity;
            firstFill.TryAdd(key, fill.PurchasedAt);
        }

        foreach (var sale in rows.Where(row => row.Kind == TransactionType.Sell))
        {
            var key = ItemNameNormalizer.Normalize(sale.ItemName);
            if (firstFill.TryGetValue(key, out var since) && sale.PurchasedAt >= since)
            {
                held[key] = held.GetValueOrDefault(key) - sale.Quantity;
            }
        }

        foreach (var listing in currentOrders.Where(order => order.Type == TransactionType.Sell && !string.IsNullOrWhiteSpace(order.ItemName)))
        {
            var key = ItemNameNormalizer.Normalize(listing.ItemName);
            if (held.ContainsKey(key))
            {
                held[key] -= listing.Quantity;
            }
        }

        return held;
    }

    /// <summary>
    /// How far GW2's creation time may be from the moment the order was confirmed on screen. Measured: under 15 s; the
    /// rest allows for a PC clock that is a little off.
    /// </summary>
    private static readonly TimeSpan CreationWindow = TimeSpan.FromSeconds(90);

    /// <summary>True when an order GW2 created at <paramref name="created"/> is the one placed at <paramref name="placedAt"/>.</summary>
    public static bool CreatedWith(DateTimeOffset created, DateTimeOffset placedAt) => (created - placedAt).Duration() <= CreationWindow;

    /// <summary>
    /// True when a current buy order is one Mmopal placed: same item, created when Mmopal placed one. Buy orders the
    /// player placed by hand are never cancelled by Outbid Refresh.
    /// </summary>
    public static bool IsMmopalsOrder(Transaction order, IEnumerable<PlacedBuyOrder> placedOrders) =>
        placedOrders.Any(placed =>
            CreatedWith(order.Created, placed.PlacedAt) &&
            string.Equals(ItemNameNormalizer.Normalize(placed.ItemName), ItemNameNormalizer.Normalize(order.ItemName), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True when every current listing of <paramref name="itemName"/> is Mmopal's: it bought at least as many units as
    /// were sold and are listed, so cancelling one to relist it cannot touch a listing the player made.
    /// </summary>
    public static bool ListingsAreMmopals(string itemName, IReadOnlyDictionary<string, long> heldByMmopal) =>
        heldByMmopal.TryGetValue(ItemNameNormalizer.Normalize(itemName), out var held) && held >= 0;

    /// <summary>
    /// True when listing this character's whole stack of <paramref name="itemName"/> sells only Mmopal's units:
    /// the stack is no larger than what Mmopal still holds.
    /// </summary>
    public static bool StackIsMmopals(string itemName, long stackUnits, IReadOnlyDictionary<string, long> heldByMmopal) =>
        stackUnits > 0 && heldByMmopal.GetValueOrDefault(ItemNameNormalizer.Normalize(itemName)) >= stackUnits;
}
