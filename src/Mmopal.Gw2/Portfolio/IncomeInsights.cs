using System.Globalization;
using Mmopal.Contracts;

namespace Mmopal.Gw2;

public sealed record NetWorthPoint(DateTimeOffset At, long ValueCopper, string Label);

public sealed record StockOnShelf(
    int Listings,
    int Undercut,
    int UndercutOver12h,
    long ListedNetCopper,
    long UnsoldCostCopper);

/// <summary>
/// The player-facing numbers on Income &amp; Portfolio. "Profit from sales" alone looks perfect (100% winners) while
/// losers sit unsold, so the page leads with net worth over time and shows the unsold stock and the fees beside it.
/// </summary>
public static class IncomeInsights
{
    /// <summary>Net worth at the first snapshot inside the window and now; null without two snapshots.</summary>
    public static (NetWorthPoint Start, NetWorthPoint End)? NetWorthChange(IReadOnlyList<Gw2EquitySnapshot> snapshots, DateTimeOffset? windowStartUtc)
    {
        var inWindow = SnapshotsInWindow(snapshots, windowStartUtc);
        if (inWindow.Length < 2)
        {
            return null;
        }

        return (ToPoint(inWindow[0], dayLabel: false), ToPoint(inWindow[^1], dayLabel: false));
    }

    /// <summary>
    /// Up to <paramref name="maxPoints"/> points: the last snapshot of each day when the window spans 3+ days,
    /// otherwise evenly spaced snapshots labelled by time.
    /// </summary>
    public static IReadOnlyList<NetWorthPoint> NetWorthSeries(IReadOnlyList<Gw2EquitySnapshot> snapshots, DateTimeOffset? windowStartUtc, int maxPoints = 24)
    {
        var inWindow = SnapshotsInWindow(snapshots, windowStartUtc);
        if (inWindow.Length == 0)
        {
            return Array.Empty<NetWorthPoint>();
        }

        var spanDays = (inWindow[^1].CapturedAt - inWindow[0].CapturedAt).TotalDays;
        if (spanDays >= 3)
        {
            return inWindow
                .GroupBy(s => s.CapturedAt.ToLocalTime().Date)
                .Select(day => ToPoint(day.Last(), dayLabel: true))
                .TakeLast(maxPoints)
                .ToArray();
        }

        if (inWindow.Length <= maxPoints)
        {
            return inWindow.Select(s => ToPoint(s, dayLabel: false)).ToArray();
        }

        var step = (inWindow.Length - 1) / (double)(maxPoints - 1);
        return Enumerable.Range(0, maxPoints)
            .Select(i => ToPoint(inWindow[(int)Math.Round(i * step)], dayLabel: false))
            .ToArray();
    }

    /// <summary>Listing (5%) and exchange (10%) fees on everything sold in the window.</summary>
    public static long FeesPaid(IEnumerable<Gw2LedgerTransaction> history, DateTimeOffset? windowStartUtc) =>
        history
            .Where(t => t.Kind == TransactionType.Sell && (windowStartUtc is null || t.PurchasedAt >= windowStartUtc))
            .Sum(t => UnitFees(t.UnitPriceCopper) * Math.Max(0, t.Quantity));

    /// <summary>Cost of bought units not sold yet (first in, first out): in bags, in the Delivery Box or listed.</summary>
    public static long UnsoldCost(IEnumerable<Gw2LedgerTransaction> history)
    {
        var lots = new Dictionary<int, List<(long Price, long Quantity)>>();
        foreach (var tx in history.OrderBy(t => t.PurchasedAt).ThenBy(t => t.Id))
        {
            if (tx.Quantity <= 0) continue;
            if (!lots.TryGetValue(tx.ItemId, out var open))
            {
                lots[tx.ItemId] = open = [];
            }

            if (tx.Kind == TransactionType.Buy)
            {
                open.Add((tx.UnitPriceCopper, tx.Quantity));
                continue;
            }

            var toMatch = tx.Quantity;
            while (toMatch > 0 && open.Count > 0)
            {
                var (price, quantity) = open[0];
                var used = Math.Min(quantity, toMatch);
                toMatch -= used;
                if (quantity > used)
                {
                    open[0] = (price, quantity - used);
                }
                else
                {
                    open.RemoveAt(0);
                }
            }
        }

        return lots.Values.Sum(open => open.Sum(lot => lot.Price * lot.Quantity));
    }

    public static StockOnShelf Shelf(IReadOnlyList<Gw2ActiveOrder> activeOrders, IEnumerable<Gw2LedgerTransaction> history, DateTimeOffset nowUtc)
    {
        var sells = activeOrders.Where(o => o.Kind == TransactionType.Sell).ToArray();
        var undercut = sells.Where(o => o.MarketLowestSellCopper is { } ask && ask > 0 && ask < o.UnitPriceCopper).ToArray();
        return new StockOnShelf(
            sells.Length,
            undercut.Length,
            undercut.Count(o => nowUtc - o.CreatedAt >= SellRelistPolicy.MinListingAge),
            sells.Sum(o => TradingIncomeAnalytics.CalculateNetSaleUnitPrice(o.UnitPriceCopper) * Math.Max(1, o.Quantity)),
            UnsoldCost(history));
    }

    public static string FormatAge(TimeSpan age)
    {
        if (age.TotalHours < 1)
        {
            return $"{Math.Max(0, (int)age.TotalMinutes)}m";
        }

        if (age.TotalHours < 48)
        {
            return $"{(int)age.TotalHours}h";
        }

        return string.Create(CultureInfo.InvariantCulture, $"{age.TotalDays:0.#}d");
    }

    /// <summary>Snapshots with a known net worth, captured inside the window (null: all), oldest first.</summary>
    private static Gw2EquitySnapshot[] SnapshotsInWindow(IReadOnlyList<Gw2EquitySnapshot> snapshots, DateTimeOffset? windowStartUtc) =>
        snapshots
            .Where(s => s.TotalEquityCopper > 0 && (windowStartUtc is null || s.CapturedAt >= windowStartUtc))
            .OrderBy(s => s.CapturedAt)
            .ToArray();

    private static long UnitFees(long unitPriceCopper) =>
        TradingIncomeAnalytics.CalculateListingFee(unitPriceCopper) + TradingIncomeAnalytics.CalculateExchangeFee(unitPriceCopper);

    private static NetWorthPoint ToPoint(Gw2EquitySnapshot snapshot, bool dayLabel)
    {
        var format = dayLabel ? "MM-dd" : "HH:mm";
        return new NetWorthPoint(
            snapshot.CapturedAt,
            snapshot.TotalEquityCopper,
            snapshot.CapturedAt.ToLocalTime().ToString(format, CultureInfo.InvariantCulture));
    }
}
