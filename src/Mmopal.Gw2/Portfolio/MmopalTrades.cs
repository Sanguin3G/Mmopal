using Mmopal.Contracts;

namespace Mmopal.Gw2;

/// <summary>
/// The trades Mmopal made, out of everything the account bought and sold: its buy fills, and the sales of as many units
/// as those bought. "Bought, not sold yet" showed 1595g where Mmopal's stock had cost 290g, and the best hour was the
/// morning the player sold ~400g of their own materials (21 Nov).
/// </summary>
public static class MmopalTrades
{
    public static IReadOnlyList<Gw2LedgerTransaction> Only(IEnumerable<Gw2LedgerTransaction> history, IEnumerable<PlacedBuyOrder> placedOrders)
    {
        var placed = placedOrders
            .GroupBy(order => ItemNameNormalizer.Normalize(order.ItemName), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(order => order.PlacedAt).ToArray(), StringComparer.OrdinalIgnoreCase);
        var unsold = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var trades = new List<Gw2LedgerTransaction>();
        foreach (var row in history.OrderBy(row => row.PurchasedAt).ThenBy(row => row.Kind))
        {
            var key = ItemNameNormalizer.Normalize(row.ItemName);
            if (row.Kind == TransactionType.Buy)
            {
                if (placed.TryGetValue(key, out var times) && times.Any(at => OwnedStock.CreatedWith(row.CreatedAt, at)))
                {
                    unsold[key] = unsold.GetValueOrDefault(key) + row.Quantity;
                    trades.Add(row);
                }

                continue;
            }

            var units = Math.Min(row.Quantity, unsold.GetValueOrDefault(key));
            if (units > 0)
            {
                unsold[key] -= units;
                trades.Add(row with { Quantity = units });
            }
        }

        return trades;
    }
}
