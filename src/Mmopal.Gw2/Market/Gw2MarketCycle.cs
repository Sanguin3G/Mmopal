using System.Globalization;
using System.Text;
using Mmopal.Contracts;

namespace Mmopal.Gw2;

// The shared NA/EU Trading Post's daily phases (reset spike, EU prime, NA prime, off-peak).
public sealed record Gw2MarketCycleInfo(
    string PhaseName,
    string Advice,
    TimeSpan TimeUntilReset,
    DateTimeOffset NextResetLocal,
    string Summary);

public static class Gw2MarketCycle
{
    public static Gw2MarketCycleInfo Describe(DateTimeOffset nowUtc, TimeZoneInfo? localTimeZone = null)
    {
        var utc = nowUtc.ToUniversalTime();
        var tz = localTimeZone ?? TimeZoneInfo.Local;
        var nextResetUtc = new DateTimeOffset(utc.Year, utc.Month, utc.Day, 0, 0, 0, TimeSpan.Zero).AddDays(1);
        var untilReset = nextResetUtc - utc;
        var nextResetLocal = TimeZoneInfo.ConvertTime(nextResetUtc, tz);
        var localNow = TimeZoneInfo.ConvertTime(utc, tz);
        var tod = utc.TimeOfDay;

        string phase;
        string advice;
        if (tod < TimeSpan.FromMinutes(90))
        {
            phase = "Daily Reset Spike";
            advice = "High insta-sell wave into Buy Orders after 00:00 UTC reset.";
        }
        else if (tod >= TimeSpan.FromHours(17) && tod < TimeSpan.FromHours(22))
        {
            phase = "EU Prime Time";
            advice = "Peak EU raids & crafting; strong sell-side velocity.";
        }
        else if (tod >= TimeSpan.FromHours(22) || tod < TimeSpan.FromHours(4))
        {
            phase = "NA Prime Time";
            advice = "Peak NA crafting & market turnover across shared NA/EU TP.";
        }
        else
        {
            phase = "Global Off-Peak";
            advice = "Lower volume; avoid panic-canceling sells and losing 5% listing fees.";
        }

        var hours = (int)untilReset.TotalHours;
        var mins = untilReset.Minutes;
        var summary = $"{phase} ({localNow:HH:mm}) • Reset in {hours}h {mins:00}m ({nextResetLocal:HH:mm} local)";
        return new Gw2MarketCycleInfo(phase, advice, untilReset, nextResetLocal, summary);
    }
}
