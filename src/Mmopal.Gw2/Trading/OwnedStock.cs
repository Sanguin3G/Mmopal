using Mmopal.Contracts;

namespace Mmopal.Gw2;

/// <summary>A live buy order Mmopal placed: the item and the unit prices the order could fill at.</summary>
public sealed record PlacedBuyOrder(string ItemName, long PlannedUnitCopper, long? TypedUnitCopper, DateTimeOffset PlacedAt);

/// <summary>
/// How many units of each item Mmopal itself still holds, so the sell pass never lists the player's own copies.
/// The sell dialog lists a whole bag stack, and an item counted as a flip item only because it was on the shopping
/// list (or once ordered by Mmopal) would take personal copies with it: food, infusions, a legendary lying around.
/// Units bought = buy fills at a price one of Mmopal's orders for that item was placed at (the GW2 history records
/// the order's unit price); minus units sold since the first such fill; minus units listed now. Listings the player
/// cancels by hand return to the count, so Mmopal keeps trading its own units.
/// </summary>
public static class OwnedStock
{
    public static IReadOnlyDictionary<string, long> UnitsHeldByMmopal(
        IEnumerable<PlacedBuyOrder> placedOrders,
        IEnumerable<Gw2LedgerTransaction> history,
        IEnumerable<Transaction> currentOrders)
    {
        var pricesByItem = placedOrders
            .GroupBy(order => ItemNameNormalizer.Normalize(order.ItemName), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group
                    .SelectMany(order => order.TypedUnitCopper is { } typed
                        ? new[] { (Price: order.PlannedUnitCopper, order.PlacedAt), (Price: typed, order.PlacedAt) }
                        : new[] { (Price: order.PlannedUnitCopper, order.PlacedAt) })
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);

        var rows = history.GroupBy(row => (row.Kind, row.Id)).Select(group => group.First()).ToArray();
        var held = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var firstFill = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        foreach (var fill in rows.Where(row => row.Kind == TransactionType.Buy).OrderBy(row => row.PurchasedAt))
        {
            var key = ItemNameNormalizer.Normalize(fill.ItemName);
            if (!pricesByItem.TryGetValue(key, out var prices) ||
                !prices.Any(order => order.Price == fill.UnitPriceCopper && order.PlacedAt <= fill.PurchasedAt.AddMinutes(10)))
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

        return held.ToDictionary(pair => pair.Key, pair => Math.Max(0, pair.Value), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when listing this character's whole stack of <paramref name="itemName"/> sells only Mmopal's units:
    /// the stack is no larger than what Mmopal still holds.
    /// </summary>
    public static bool StackIsMmopals(string itemName, long stackUnits, IReadOnlyDictionary<string, long> heldByMmopal) =>
        stackUnits > 0 && heldByMmopal.GetValueOrDefault(ItemNameNormalizer.Normalize(itemName)) >= stackUnits;
}
