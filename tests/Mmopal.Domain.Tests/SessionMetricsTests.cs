using Mmopal.Contracts;
using Mmopal.Gw2;
using Mmopal.Infrastructure;

namespace Mmopal.Domain.Tests;

public sealed class SessionMetricsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset Midnight = new(2026, 11, 23, 0, 0, 0, TimeSpan.Zero);

    private static CycleMetrics Cycle(int minute, int accepted, string? skip = null, string? character = "Garry Vakarian") => new(
        T0.AddMinutes(minute),
        character,
        "Global Off-Peak",
        new Dictionary<string, double> { ["buy"] = 60, ["sell"] = 30, ["pause"] = 50 },
        Evaluated: 100,
        accepted,
        skip is null ? new Dictionary<string, int>() : new Dictionary<string, int> { [skip] = 100 },
        WalletCopper: 5_000_000,
        BuyOrdersCopper: 4_270_000,
        OldBuyOrdersCopper: 3_330_000,
        OpenBuys: 70,
        OutbidBuys: 20,
        ListingsNetCopper: 3_000_000,
        OpenListings: 86,
        UndercutListings: 30,
        SellQueue: 4,
        UnsoldUnits: 12,
        WaitingOnStock: 5);

    private static SessionEvent Event(string kind, string outcome, string? item = null, string? character = null) =>
        new(T0, kind, outcome, item, character);

    [Fact]
    public void Findings_name_stuck_gold_wasted_searches_and_repeated_aborts()
    {
        // The shape of the 22-23 Nov night: the gold cap blocked most cycles and items were searched for in vain.
        var cycles = Enumerable.Range(0, 8).Select(i => Cycle(i * 2, accepted: i < 2 ? 3 : 0, i < 2 ? null : "Buy orders already hold 427g of 854g")).ToArray();
        var events = Enumerable.Repeat(Event("sell", "not-found", "Impaler"), 12)
            .Concat(Enumerable.Repeat(Event("delivery", "locked", character: "Garry Vakarian"), 4))
            .Concat(Enumerable.Repeat(Event("buy", "title-unreadable", "Genesis"), 3))
            .Concat(Enumerable.Repeat(Event("buy", "placed", "Pyre Dye"), 6))
            .ToArray();

        var findings = SessionInsights.Findings(cycles, events);

        Assert.Contains(findings, f => f.StartsWith("The gold cap left 6 of 8 cycles", StringComparison.Ordinal) && f.Contains("333g", StringComparison.Ordinal));
        Assert.Contains(findings, f => f.StartsWith("12 searches", StringComparison.Ordinal));
        Assert.Contains(findings, f => f.Contains("locked 4 times: Garry Vakarian ×4", StringComparison.Ordinal));
        Assert.Contains(findings, f => f.StartsWith("3 of 9 buys stopped: title-unreadable (Genesis ×3)", StringComparison.Ordinal));
    }

    [Fact]
    public void A_clean_session_says_so_and_an_unmeasured_one_says_nothing()
    {
        Assert.Equal([SessionInsights.NothingStoodOut], SessionInsights.Findings([Cycle(0, 3), Cycle(3, 3)], [Event("buy", "placed", "Pyre Dye")]));
        Assert.Empty(SessionInsights.Findings([], []));
    }

    [Theory]
    [InlineData("Buy order aborted: the item window's title could not be read, and the live price is unknown.", "title-unreadable")]
    [InlineData("Buy order aborted: live top bid 61s 20c + 1c = 61s 21c is above the profitable ceiling 60s 50c; someone outbid past the margin.", "outbid-past-profit")]
    [InlineData("Listing aborted: typed 81s 98c twice but the dialog total shows 27s 87c.", "total-mismatch")]
    [InlineData("Paused by operator before searching item.", "paused")]
    [InlineData(null, "other")]
    public void Reasons_become_stable_codes(string? message, string code) => Assert.Equal(code, SessionInsights.ReasonCode(message));

    [Fact]
    public void Hours_are_rated_by_the_time_run_in_them()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("UTC+7", TimeSpan.FromHours(7), "UTC+7", "UTC+7");
        Gw2LedgerTransaction Sale(double utcHour) => new((long)(utcHour * 60), TransactionType.Sell, 1, "Pyre Dye", 10_000, 1, T0, Midnight.AddHours(utcHour));
        (DateTimeOffset, DateTimeOffset)[] sessions =
        [
            (Midnight.AddHours(18), Midnight.AddHours(20.5)),             // 01:00-03:30 local
            (Midnight.AddDays(-1).AddHours(18), Midnight.AddDays(-1).AddHours(19)),
        ];

        var points = RunningHours.Compute([Sale(18.2), Sale(18.4), Sale(19.5), Sale(22)], sessions, zone);

        Assert.Equal(2, points[1].HoursRun, 3);
        Assert.Equal(1, points[1].SellFillsPerHour, 3);       // two sales in the two hours run at 01h
        Assert.Equal(1, points[2].SellFillsPerHour, 3);
        Assert.Equal(0, points[5].SellFillsPerHour);          // sold while Mmopal was off: not a rate
        Assert.Equal(1, RunningHours.Best(points)!.LocalHour); // 02h has only 1 h of running time
    }

    [Fact]
    public async Task Cycles_and_events_are_stored_per_run()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mmopal-metrics-{Guid.NewGuid():N}.db");
        try
        {
            var store = new SessionMetricsStore(path);
            await store.InitializeAsync();
            var recent = Cycle(0, 3) with { At = DateTimeOffset.UtcNow };
            await store.SaveAsync("run", recent, [Event("sell", "listed", "Pyre Dye") with { At = DateTimeOffset.UtcNow, UnitCopper = 13_987 }]);
            await store.SaveAsync("other", null, [Event("buy", "placed", "Ebonblade") with { At = DateTimeOffset.UtcNow }]);

            var cycle = Assert.Single(await store.GetCyclesAsync("run"));
            Assert.Equal(60, cycle.Seconds["buy"]);
            Assert.Equal(3_330_000, cycle.OldBuyOrdersCopper);
            var sale = Assert.Single(await store.GetEventsAsync("run"));
            Assert.Equal(13_987, sale.UnitCopper);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    [Fact]
    public void Only_one_holder_identifies_the_logged_in_character()
    {
        var bags = new Dictionary<string, IReadOnlySet<string>>
        {
            ["Mesmerising Rizzo"] = new HashSet<string> { "Ghost Dye", "Twin Sisters" },
            ["Garry Vakarian"] = new HashSet<string> { "Twin Sisters" },
        };

        Assert.Equal("Mesmerising Rizzo", CharacterStock.OnlyHolder("Ghost Dye", bags));
        Assert.Null(CharacterStock.OnlyHolder("Twin Sisters", bags));
        Assert.Null(CharacterStock.OnlyHolder("Pyre Dye", bags));
    }
}
