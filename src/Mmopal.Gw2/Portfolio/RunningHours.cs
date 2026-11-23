using Mmopal.Contracts;

namespace Mmopal.Gw2;

/// <summary>Trading results of one hour of the day, per hour Mmopal was running then.</summary>
public sealed record RunningHourPoint(int LocalHour, double HoursRun, double BuyFillsPerHour, double SellFillsPerHour, long SoldNetPerHourCopper);

/// <summary>
/// Which hours of the day trade best. Raw totals per hour mostly show when Mmopal happened to run (19-23h looked dead
/// in November only because it was never run then), so fills are divided by the time it ran in that hour.
/// </summary>
public static class RunningHours
{
    /// <summary>An hour needs this much running time before its rate means something.</summary>
    public const double MinHoursRun = 2;

    public static IReadOnlyList<RunningHourPoint> Compute(
        IEnumerable<Gw2LedgerTransaction> transactions,
        IEnumerable<(DateTimeOffset Start, DateTimeOffset End)> sessions,
        TimeZoneInfo? timeZone = null)
    {
        var zone = timeZone ?? TimeZoneInfo.Local;
        var ranges = sessions.Where(session => session.End > session.Start).ToArray();
        var hoursRun = new double[24];
        foreach (var (start, end) in ranges)
        {
            for (var at = start; at < end;)
            {
                var local = TimeZoneInfo.ConvertTime(at, zone);
                var next = at + (TimeSpan.FromHours(1) - TimeSpan.FromTicks(local.TimeOfDay.Ticks % TimeSpan.TicksPerHour));
                if (next > end) next = end;
                hoursRun[local.Hour] += (next - at).TotalHours;
                at = next;
            }
        }

        var buys = new int[24];
        var sells = new int[24];
        var net = new long[24];
        foreach (var trade in transactions.Where(trade => ranges.Any(range => trade.PurchasedAt >= range.Start && trade.PurchasedAt <= range.End)))
        {
            var hour = TimeZoneInfo.ConvertTime(trade.PurchasedAt, zone).Hour;
            if (trade.Kind == TransactionType.Buy)
            {
                buys[hour]++;
            }
            else
            {
                sells[hour]++;
                net[hour] += TradingIncomeAnalytics.CalculateNetSaleUnitPrice(trade.UnitPriceCopper) * trade.Quantity;
            }
        }

        return Enumerable.Range(0, 24)
            .Select(hour => hoursRun[hour] < 0.25
                ? new RunningHourPoint(hour, hoursRun[hour], 0, 0, 0)
                : new RunningHourPoint(hour, hoursRun[hour], buys[hour] / hoursRun[hour], sells[hour] / hoursRun[hour], (long)(net[hour] / hoursRun[hour])))
            .ToArray();
    }

    /// <summary>The best hour among those with enough running time, or null.</summary>
    public static RunningHourPoint? Best(IEnumerable<RunningHourPoint> points) =>
        points.Where(point => point.HoursRun >= MinHoursRun && point.SoldNetPerHourCopper > 0).MaxBy(point => point.SoldNetPerHourCopper);
}
