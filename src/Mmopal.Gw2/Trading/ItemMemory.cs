using Mmopal.Contracts;

namespace Mmopal.Gw2;

/// <summary>What the account's own trades say about one item over the last two weeks.</summary>
/// <param name="UnitsOrdered">Units in Mmopal's buy orders old enough to have had their chance.</param>
/// <param name="UnitsFilled">Units of those orders that filled.</param>
/// <param name="UnitsSold">Units sold since the first fill.</param>
/// <param name="Margin">What a sold unit earned after fees, as a share of what it cost (0.21 = 21%); null before two sales.</param>
public sealed record ItemRecord(long UnitsOrdered, long UnitsFilled, long UnitsSold, double? Margin);

/// <summary>
/// The account's memory of every item it traded, and the order the shopping list is worked in. An item's own margin
/// carries over well (correlation 0.84 between its flips before and after 25 Nov, 72 items) and so does whether its orders
/// fill; how fast it sells does not (0.02). The list is ranked by the return a gold placed in an order is expected to bring:
/// fill share times margin, both from the item's own record, pulled towards the account's average while that record is
/// short. At the gold cap the most productive orders then come first: per 100g held an hour, items under 50s earned 3.1g
/// and items of 8g+ 1.6g (20-27 Nov).
/// </summary>
public sealed class ItemMemory
{
    public static readonly TimeSpan Window = TimeSpan.FromDays(14);
    // Units an item's record has to show before it outweighs the account's average.
    private const double PriorUnits = 6;
    private const double PriorSales = 4;

    private readonly IReadOnlyDictionary<string, ItemRecord> _items;

    private ItemMemory(IReadOnlyDictionary<string, ItemRecord> items, double fillShare, double margin)
    {
        _items = items;
        FillShare = fillShare;
        Margin = margin;
    }

    public static readonly ItemMemory Empty = new(new Dictionary<string, ItemRecord>(), 0.33, 0.2);

    /// <summary>The account's average share of ordered units that fill.</summary>
    public double FillShare { get; }

    /// <summary>The account's average margin per sold unit.</summary>
    public double Margin { get; }

    public int Count => _items.Count;

    public ItemRecord? this[string itemName] => _items.GetValueOrDefault(ItemNameNormalizer.Normalize(itemName));

    public static ItemMemory Build(IEnumerable<PlacedBuyOrder> placedOrders, IEnumerable<Gw2LedgerTransaction> history, DateTimeOffset nowUtc)
    {
        var since = nowUtc - Window;
        var rows = history
            .Where(row => row.PurchasedAt >= since)
            .GroupBy(row => (row.Kind, row.Id)).Select(group => group.First())
            .ToLookup(row => ItemNameNormalizer.Normalize(row.ItemName), StringComparer.OrdinalIgnoreCase);
        var items = new Dictionary<string, ItemRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in placedOrders.Where(order => order.PlacedAt >= since).GroupBy(order => ItemNameNormalizer.Normalize(order.ItemName), StringComparer.OrdinalIgnoreCase))
        {
            var fills = rows[item.Key].Where(row => row.Kind == TransactionType.Buy).ToArray();
            long ordered = 0, filled = 0;
            foreach (var order in item)
            {
                var got = fills.Where(fill => OwnedStock.CreatedWith(fill.CreatedAt, order.PlacedAt)).Sum(fill => fill.Quantity);
                var size = order.Quantity ?? 1;
                if (got < size && nowUtc - order.PlacedAt < ProvenItems.TooYoung) continue; // still has its chance
                ordered += size;
                filled += Math.Min(got, size);
            }

            var mine = fills.Where(fill => item.Any(order => OwnedStock.CreatedWith(fill.CreatedAt, order.PlacedAt))).ToArray();
            var firstFill = mine.Length == 0 ? DateTimeOffset.MaxValue : mine.Min(fill => fill.PurchasedAt);
            var sales = rows[item.Key].Where(row => row.Kind == TransactionType.Sell && row.PurchasedAt >= firstFill && row.Quantity > 0).ToArray();
            var sold = sales.Sum(sale => sale.Quantity);
            double? margin = null;
            if (sold >= 2 && mine.Length > 0)
            {
                var paid = mine.Sum(fill => (double)fill.UnitPriceCopper * fill.Quantity) / mine.Sum(fill => fill.Quantity);
                var earned = sales.Sum(sale => (double)TradingIncomeAnalytics.CalculateNetSaleUnitPrice(sale.UnitPriceCopper) * sale.Quantity) / sold;
                margin = paid > 0 ? (earned - paid) / paid : null;
            }

            if (ordered > 0 || margin is not null)
            {
                items[item.Key] = new ItemRecord(ordered, filled, sold, margin);
            }
        }

        var allOrdered = items.Values.Sum(record => record.UnitsOrdered);
        var withMargin = items.Values.Where(record => record.Margin is not null).ToArray();
        return new ItemMemory(
            items,
            allOrdered >= 20 ? (double)items.Values.Sum(record => record.UnitsFilled) / allOrdered : Empty.FillShare,
            withMargin.Length >= 5 ? withMargin.Average(record => record.Margin!.Value) : Empty.Margin);
    }

    /// <summary>
    /// The return a gold placed in an order of this item is expected to bring: its fill share times its margin, each pulled
    /// towards the account's average by how little the item's own record shows. Without a record the scanner's margin is
    /// used (<paramref name="scannerMargin"/>, after fees).
    /// </summary>
    public double ExpectedReturn(string itemName, double? scannerMargin)
    {
        var record = this[itemName];
        var fill = ((record?.UnitsFilled ?? 0) + FillShare * PriorUnits) / ((record?.UnitsOrdered ?? 0) + PriorUnits);
        // The scanner's margin is the spread before anyone competes for it; capped near what the account really earns.
        var prior = scannerMargin is { } scanned ? Math.Min(scanned, Margin * 1.5) : Margin;
        var sales = Math.Min(record?.UnitsSold ?? 0, 50);
        var margin = record?.Margin is { } own ? (own * sales + prior * PriorSales) / (sales + PriorSales) : prior;
        return fill * margin;
    }

    /// <summary>The candidates, best expected return first; the order among equals is kept.</summary>
    public IReadOnlyList<ItemCandidate> Rank(IReadOnlyList<ItemCandidate> candidates) =>
        candidates
            .Select((candidate, index) => (candidate, index, Score: ExpectedReturn(candidate.ItemName, candidate.Roi is { } roi ? (double)roi / 100 : null)))
            .OrderByDescending(entry => entry.Score)
            .ThenBy(entry => entry.index)
            .Select(entry => entry.candidate)
            .ToArray();
}
