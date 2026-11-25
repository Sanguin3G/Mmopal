using Mmopal.Contracts;

namespace Mmopal.Gw2;

/// <summary>
/// Items whose own record earns them larger orders: at least three orders, half of them filled completely, sold at a
/// profit and mostly within four hours. Items with that record before the night of 24 Nov filled 42 of 68 orders of two
/// completely, the rest 115 of 385; those that also sell fast had earned 121g of 421g, mostly two units at a time.
/// </summary>
public static class ProvenItems
{
    public static readonly TimeSpan Window = TimeSpan.FromDays(14);
    /// <summary>An order this young has not had its chance to fill yet.</summary>
    public static readonly TimeSpan TooYoung = TimeSpan.FromHours(2);
    public const int MinOrders = 3;
    public const double MinFullFillShare = 0.5;
    public const long MinUnitsSold = 4;
    public static readonly TimeSpan MaxMedianTimeToSell = TimeSpan.FromHours(4);
    public const long LargestOrder = 20;

    public static IReadOnlySet<string> Find(IEnumerable<PlacedBuyOrder> placedOrders, IEnumerable<Gw2LedgerTransaction> history, DateTimeOffset nowUtc)
    {
        var since = nowUtc - Window;
        var rows = history
            .Where(row => row.PurchasedAt >= since)
            .GroupBy(row => (row.Kind, row.Id)).Select(group => group.First())
            .ToLookup(row => ItemNameNormalizer.Normalize(row.ItemName), StringComparer.OrdinalIgnoreCase);
        var proven = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in placedOrders.Where(order => order.PlacedAt >= since).GroupBy(order => ItemNameNormalizer.Normalize(order.ItemName), StringComparer.OrdinalIgnoreCase))
        {
            var fills = rows[item.Key].Where(row => row.Kind == TransactionType.Buy).ToArray();
            var sales = rows[item.Key].Where(row => row.Kind == TransactionType.Sell && row.Quantity > 0).ToArray();
            var orders = item
                .Select(order => (Order: order, Filled: fills.Where(fill => OwnedStock.CreatedWith(fill.CreatedAt, order.PlacedAt)).Sum(fill => fill.Quantity)))
                .Select(entry => (entry.Order, Full: entry.Filled >= (entry.Order.Quantity ?? 1)))
                .Where(entry => entry.Full || nowUtc - entry.Order.PlacedAt >= TooYoung)
                .ToArray();
            if (orders.Length < MinOrders ||
                orders.Count(entry => entry.Full) < orders.Length * MinFullFillShare ||
                sales.Sum(sale => sale.Quantity) < MinUnitsSold ||
                MedianTimeToSell(sales) >= MaxMedianTimeToSell)
            {
                continue;
            }

            var paid = fills.Sum(fill => (decimal)fill.UnitPriceCopper * fill.Quantity) / Math.Max(1, fills.Sum(fill => fill.Quantity));
            var earned = sales.Sum(sale => (decimal)TradingIncomeAnalytics.CalculateNetSaleUnitPrice(sale.UnitPriceCopper) * sale.Quantity) / sales.Sum(sale => sale.Quantity);
            if (earned > paid)
            {
                proven.Add(item.Key);
            }
        }

        return proven;
    }

    /// <summary>
    /// Doubles the order of proven items: no dearer than the budget for one order, at most <see cref="LargestOrder"/>
    /// units. Not while two orders' worth is still listed, and never an item whose size the player set.
    /// </summary>
    public static IReadOnlyList<ItemCandidate> Enlarge(
        IReadOnlyList<ItemCandidate> candidates,
        IReadOnlySet<string> proven,
        IEnumerable<Transaction> currentOrders,
        IEnumerable<string> playersOwnList,
        long defaultQuantity,
        long orderBudgetCopper)
    {
        if (proven.Count == 0)
        {
            return candidates;
        }

        var own = playersOwnList.Select(name => ItemNameNormalizer.Normalize(name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var listed = currentOrders
            .Where(order => order.Type == TransactionType.Sell && !string.IsNullOrWhiteSpace(order.ItemName))
            .GroupBy(order => ItemNameNormalizer.Normalize(order.ItemName), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Sum(order => order.Quantity), StringComparer.OrdinalIgnoreCase);
        return candidates.Select(candidate =>
        {
            var key = ItemNameNormalizer.Normalize(candidate.ItemName);
            var size = Math.Max(1, candidate.Quantity ?? defaultQuantity);
            if (!proven.Contains(key) || own.Contains(key) || candidate.BuyPriceCopper is not > 0 || listed.GetValueOrDefault(key) >= size * 2)
            {
                return candidate;
            }

            var larger = Math.Min(Math.Min(size * 2, LargestOrder), orderBudgetCopper / candidate.BuyPriceCopper.Value);
            return larger > size ? candidate with { Quantity = larger } : candidate;
        }).ToArray();
    }

    private static TimeSpan MedianTimeToSell(IReadOnlyList<Gw2LedgerTransaction> sales)
    {
        var times = sales
            .SelectMany(sale => Enumerable.Repeat(sale.PurchasedAt - sale.CreatedAt, (int)Math.Min(sale.Quantity, 250)))
            .OrderBy(time => time)
            .ToArray();
        return times[times.Length / 2];
    }
}
