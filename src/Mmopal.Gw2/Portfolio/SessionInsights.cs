using Mmopal.Contracts;

namespace Mmopal.Gw2;

/// <summary>
/// What a session's recorded cycles and events say about how well it ran. These are the questions that were answered
/// by hand from the log after every long run: is gold stuck, where does the time go, what keeps failing.
/// </summary>
public static class SessionInsights
{
    public const string NothingStoodOut = "Nothing stood out: no step failed often and no gold was stuck.";

    /// <summary>Share of cycles (or attempts) from which something counts as a pattern.</summary>
    private const double PatternShare = 0.25;

    /// <summary>A short, stable code for why a buy or listing did not happen, so runs can be counted together.</summary>
    public static string ReasonCode(string? message)
    {
        var text = message ?? string.Empty;
        return Codes.FirstOrDefault(code => text.Contains(code.Text, StringComparison.OrdinalIgnoreCase)).Code ?? "other";
    }

    private static readonly (string Text, string Code)[] Codes =
    [
        ("Paused", "paused"),
        ("title could not be read", "title-unreadable"),
        ("None of the search rows opened", "wrong-item"),
        ("opened a different item", "wrong-item"),
        ("above the profitable ceiling", "outbid-past-profit"),
        ("total could not be read", "total-unreadable"),
        ("the dialog total", "total-mismatch"),
        ("could not be read", "price-unreadable"),
        ("price boxes are not on screen", "price-boxes-missing"),
        ("modal did not open", "dialog-not-open"),
        ("not detected", "button-missing"),
        ("unavailable", "screen-missing"),
        ("below break-even", "below-cost"),
        ("Not present in current character bags", "not-found"),
        ("No sellable item row", "not-found")
    ];

    /// <summary>Total seconds per step over the cycles, longest first.</summary>
    public static IReadOnlyDictionary<string, double> SecondsByStep(IReadOnlyList<CycleMetrics> cycles) =>
        cycles
            .SelectMany(cycle => cycle.Seconds)
            .GroupBy(step => step.Key)
            .OrderByDescending(group => group.Sum(step => step.Value))
            .ToDictionary(group => group.Key, group => Math.Round(group.Sum(step => step.Value), 1));

    /// <summary>How often each kind of event ended how, for example "sell: not-found" → 7.</summary>
    public static IReadOnlyDictionary<string, int> OutcomeCounts(IReadOnlyList<SessionEvent> events) =>
        events
            .GroupBy(item => $"{item.Kind}: {item.Outcome}")
            .OrderByDescending(group => group.Count())
            .ToDictionary(group => group.Key, group => group.Count());

    public static IReadOnlyList<string> Findings(IReadOnlyList<CycleMetrics> cycles, IReadOnlyList<SessionEvent> events)
    {
        if (cycles.Count == 0)
        {
            return [];
        }

        var findings = new List<string>();
        var last = cycles[^1];
        bool Often(int count) => count >= 3 && count >= cycles.Count * PatternShare;

        var capped = cycles.Count(cycle => cycle.Accepted == 0 && cycle.SkipReasons.Keys.Any(reason => reason.StartsWith("Buy orders already hold", StringComparison.Ordinal)));
        if (Often(capped))
        {
            findings.Add($"The gold cap left {capped} of {cycles.Count} cycles with nothing to buy; {Coins(last.OldBuyOrdersCopper)} of the {Coins(last.BuyOrdersCopper)} in buy orders has waited over 12 h.");
        }

        var idle = cycles.Count(cycle => cycle.Accepted == 0 && cycle.Evaluated > 0);
        if (Often(idle - capped))
        {
            var reason = cycles.Where(cycle => cycle.Accepted == 0).SelectMany(cycle => cycle.SkipReasons).GroupBy(pair => pair.Key).OrderByDescending(group => group.Sum(pair => pair.Value)).FirstOrDefault()?.Key;
            findings.Add($"{idle - capped} of {cycles.Count} cycles had nothing to buy, mostly: {reason ?? "no reason recorded"}.");
        }

        AddOften(findings, events, "sell", "not-found", 10, count => $"{count} searches for items that are not in this character's bags (about {count * 10 / 60.0:0} min).");
        AddOften(findings, events, "cancel", "unconfirmed", 5, count => $"{count} cancel searches could not be confirmed on screen: {Items(events, "cancel", "unconfirmed")}.");
        AddOften(findings, events, "delivery", "locked", 3, count => $"The Delivery Box was locked {count} times: {Characters(events, "delivery", "locked")}.");

        var buys = events.Where(item => item.Kind == "buy" && item.Outcome is not ("paused" or "simulated")).ToArray();
        foreach (var group in buys.Where(item => item.Outcome != "placed").GroupBy(item => item.Outcome).Where(group => group.Count() >= 3 && group.Count() >= buys.Length * 0.1))
        {
            findings.Add($"{group.Count()} of {buys.Length} buys stopped: {group.Key} ({Items(group)}).");
        }

        if (last.OpenListings >= 10 && last.UndercutListings >= last.OpenListings * 0.7)
        {
            var relisted = events.Count(item => item is { Kind: "relist", Outcome: "cancelled" });
            findings.Add($"{last.UndercutListings} of {last.OpenListings} listings are undercut; {relisted} were relisted.");
        }

        if (last.OpenBuys >= 10 && last.OutbidBuys >= last.OpenBuys * 0.6)
        {
            findings.Add($"{last.OutbidBuys} of {last.OpenBuys} buy orders are outbid.");
        }

        var unknown = cycles.Count(cycle => cycle.Character is null);
        if (Often(unknown))
        {
            findings.Add($"The logged-in character was unknown in {unknown} of {cycles.Count} cycles, so stock on other characters was searched for here.");
        }

        var seconds = SecondsByStep(cycles);
        var total = seconds.Values.Sum();
        foreach (var (step, share) in seconds.Where(pair => pair.Key is not ("buy" or "sell" or "pause")).Select(pair => (pair.Key, pair.Value / Math.Max(1, total))).Where(pair => pair.Item2 >= PatternShare))
        {
            findings.Add($"{share:P0} of the time went to '{step}'.");
        }

        if (findings.Count == 0)
        {
            findings.Add(NothingStoodOut);
        }

        return findings;
    }

    private static void AddOften(List<string> findings, IReadOnlyList<SessionEvent> events, string kind, string outcome, int minimum, Func<int, string> describe)
    {
        var count = events.Count(item => item.Kind == kind && item.Outcome == outcome);
        if (count >= minimum)
        {
            findings.Add(describe(count));
        }
    }

    private static string Items(IReadOnlyList<SessionEvent> events, string kind, string outcome) =>
        Items(events.Where(item => item.Kind == kind && item.Outcome == outcome));

    private static string Items(IEnumerable<SessionEvent> events) => TopNames(events.Select(item => item.ItemName));

    private static string Characters(IReadOnlyList<SessionEvent> events, string kind, string outcome) =>
        TopNames(events.Where(item => item.Kind == kind && item.Outcome == outcome).Select(item => item.Character ?? "unknown character"));

    private static string TopNames(IEnumerable<string?> names) =>
        string.Join(", ", names
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .GroupBy(name => name!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .Take(3)
            .Select(group => group.Count() > 1 ? $"{group.Key} ×{group.Count()}" : group.Key));

    private static string Coins(long copper) => MarketScanItem.FormatCoins(copper);
}
