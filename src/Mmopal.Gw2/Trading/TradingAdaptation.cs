using Mmopal.Contracts;

namespace Mmopal.Gw2;

/// <summary>
/// "Bot war" guard. Some items are camped by other bots that re-outbid every few minutes: in one 7 h session
/// Super Sharpening Polygon got 12 buy orders, 11 outbid cancels and 0 fills, and ~100 orders went to items that never
/// filled. After <see cref="OutbidCancelsBeforePause"/> outbid cancels without a single fill, the item is paused for
/// <see cref="PauseDuration"/> so those order slots go to items that actually fill. A fill resets the count. Each further
/// pause without a fill is twice as long, up to <see cref="LongestPause"/>: on 24 Nov 41 items took 161 of 456 orders
/// without filling once, coming back every 3 h.
/// </summary>
public sealed class BuyCompetitionTracker
{
    public const int OutbidCancelsBeforePause = 3;
    public static readonly TimeSpan PauseDuration = TimeSpan.FromHours(3);
    public static readonly TimeSpan LongestPause = TimeSpan.FromHours(24);
    /// <summary>Outbid cancels in the last day, without a fill, that pause an item when a session starts.</summary>
    public const int OutbidsBeforeSeededPause = 6;
    public static readonly TimeSpan SeededPause = TimeSpan.FromHours(6);
    private readonly Dictionary<string, int> _pausesSinceFill = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, (string Name, int Count)> _cancelsSinceFill = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string Name, DateTimeOffset Until)> _paused = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Counts outbid cancels; returns the items that just reached the limit and are now paused.</summary>
    public IReadOnlyList<string> RecordOutbidCancels(IEnumerable<string> itemNames, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(itemNames);
        var newlyPaused = new List<string>();
        foreach (var raw in itemNames)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var key = ItemNameNormalizer.Normalize(raw);
            var (name, count) = _cancelsSinceFill.TryGetValue(key, out var seen) ? seen : (raw.Trim(), 0);
            count++;
            if (count >= OutbidCancelsBeforePause)
            {
                _cancelsSinceFill.Remove(key);
                var earlier = _pausesSinceFill.GetValueOrDefault(key);
                _pausesSinceFill[key] = earlier + 1;
                var pause = TimeSpan.FromTicks(Math.Min(LongestPause.Ticks, PauseDuration.Ticks << Math.Min(earlier, 4)));
                _paused[key] = (name, nowUtc + pause);
                newlyPaused.Add(name);
            }
            else
            {
                _cancelsSinceFill[key] = (name, count);
            }
        }

        return newlyPaused;
    }

    /// <summary>
    /// True for a buy aborted because the live top bid had already passed our profitable ceiling: the same bot war as
    /// an outbid cancel, caught one step earlier. Berserker's Ring was aborted this way 10 times in one 10 h session
    /// without ever counting toward the pause.
    /// </summary>
    public static bool IsOutbidPastCeiling(TradingActionResult result) =>
        result is { Status: TradingActionStatus.Failed, Action.Kind: TradingActionKind.PlaceBuyOrder } &&
        result.Message?.Contains("is above the profitable ceiling", StringComparison.Ordinal) == true;

    /// <summary>How long an item whose top-bid order sat unfilled for 2 days is not bought again.</summary>
    public static readonly TimeSpan SlowItemPause = TimeSpan.FromHours(12);

    /// <summary>
    /// Pauses items whose buy order was cancelled for not filling, so the next cycle does not place the same order
    /// again. Not a bot war: the outbid counts are left alone. A fill lifts it like any pause.
    /// </summary>
    public void PauseSlowItems(IEnumerable<string> itemNames, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(itemNames);
        foreach (var raw in itemNames.Where(name => !string.IsNullOrWhiteSpace(name)))
        {
            _paused[ItemNameNormalizer.Normalize(raw)] = (raw.Trim(), nowUtc + SlowItemPause);
        }
    }

    public void RecordFills(IEnumerable<string> itemNames)
    {
        ArgumentNullException.ThrowIfNull(itemNames);
        foreach (var raw in itemNames)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var key = ItemNameNormalizer.Normalize(raw);
            _cancelsSinceFill.Remove(key);
            _pausesSinceFill.Remove(key);
            _paused.Remove(key);
        }
    }

    /// <summary>How long the item is still paused.</summary>
    public TimeSpan PausedFor(string itemName, DateTimeOffset nowUtc) =>
        _paused.TryGetValue(ItemNameNormalizer.Normalize(itemName), out var entry) && entry.Until > nowUtc ? entry.Until - nowUtc : TimeSpan.Zero;

    /// <summary>Pauses items that were outbid often in the last day and never filled; returns their names.</summary>
    public IReadOnlyList<string> SeedFromLastDay(IReadOnlyDictionary<string, int> outbidCancels, IEnumerable<string> filledItems, DateTimeOffset nowUtc)
    {
        var filled = filledItems.Select(name => ItemNameNormalizer.Normalize(name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var paused = new List<string>();
        foreach (var (name, count) in outbidCancels.Where(pair => pair.Value >= OutbidsBeforeSeededPause))
        {
            var key = ItemNameNormalizer.Normalize(name);
            if (filled.Contains(key)) continue;
            _paused[key] = (name, nowUtc + SeededPause);
            _pausesSinceFill[key] = 1;
            paused.Add(name);
        }

        return paused;
    }

    public bool IsPaused(string itemName, DateTimeOffset nowUtc) =>
        !string.IsNullOrWhiteSpace(itemName) &&
        _paused.TryGetValue(ItemNameNormalizer.Normalize(itemName), out var entry) &&
        entry.Until > nowUtc;

    public IReadOnlyList<string> PausedItems(DateTimeOffset nowUtc) =>
        _paused.Values.Where(entry => entry.Until > nowUtc).Select(entry => entry.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray();
}

/// <summary>A listing worth cancelling and relisting, and the price it would be relisted at.</summary>
public sealed record SellRelist(Transaction Listing, long NewPriceCopper, string Reason);

/// <summary>
/// When to cancel and relist an undercut listing. Each relist pays a new 5% listing fee (the old one is lost), so this
/// is deliberately narrow: listed 12 h+, undercut, an item that actually trades (on the scanner's shopping list; any
/// item after 24 h),
/// relisted at most once a day, and only when the new price still covers what was paid plus the lost listing fee
/// after the new 5% + 10% fees. Anything else waits: a listing that sits costs nothing more.
/// </summary>
public static class SellRelistPolicy
{
    public static readonly TimeSpan MinListingAge = TimeSpan.FromHours(12);
    /// <summary>
    /// From here the item need not be on the list any more: of 938 listings (22-25 Nov) 74% an hour sold in the first hour,
    /// 5% an hour up to a day, 0.5% after it. 40g sat that way for 30 h+, every one sellable at a profit.
    /// </summary>
    public static readonly TimeSpan StaleListingAge = TimeSpan.FromHours(24);
    public static readonly TimeSpan MinTimeBetweenRelists = TimeSpan.FromHours(24);

    public static IReadOnlyList<SellRelist> Select(
        IReadOnlyList<Transaction> orders,
        IReadOnlyDictionary<string, long> paidByItem,
        IReadOnlySet<string> tradedItems,
        IReadOnlyDictionary<string, DateTimeOffset> lastRelistedUtc,
        int undercutCopper,
        DateTimeOffset nowUtc,
        int maxPerPass)
    {
        ArgumentNullException.ThrowIfNull(orders);
        var picks = new List<SellRelist>();
        if (maxPerPass <= 0)
        {
            return picks;
        }

        var sellsByItem = orders
            .Where(order => order.Type == TransactionType.Sell && !string.IsNullOrWhiteSpace(order.ItemName))
            .GroupBy(order => order.ItemName.Trim(), StringComparer.OrdinalIgnoreCase);
        foreach (var group in sellsByItem.OrderBy(g => g.Min(order => order.Created)))
        {
            if (picks.Count >= maxPerPass) break;
            var name = group.Key;
            // The cancel flow clicks the first Cancel button its search lists. With several of our listings that is often
            // not the stale one: on 22 Nov Pyre Dye (3 listings) was "cancelled" twice while its 41 h old 1g 39s 88c
            // listing stayed up. So every listing must qualify (undercut, old enough), and the fee counted as lost is
            // the most expensive one's. Skipping these items altogether left 51 of 79 listings never relisted.
            var dearest = group.OrderByDescending(order => order.PriceCopper).First();
            var age = nowUtc - group.Max(order => order.Created);
            if (group.Any(order => order.MinimumSellPriceCopper is not { } ask || ask <= 0 || ask >= order.PriceCopper))
            {
                continue; // one of our listings is at (or is) the lowest ask, or the market is unknown: it can still sell
            }

            if (age < MinListingAge ||
                (age < StaleListingAge && !tradedItems.Contains(name)) ||
                !paidByItem.TryGetValue(name, out var paid) || paid <= 0 ||
                (lastRelistedUtc.TryGetValue(name, out var last) && nowUtc - last < MinTimeBetweenRelists))
            {
                continue;
            }

            var lowestAsk = dearest.MinimumSellPriceCopper!.Value;
            var newPrice = Math.Max(1, lowestAsk - Math.Max(0, undercutCopper));
            var lostFee = TradingIncomeAnalytics.CalculateListingFee(dearest.PriceCopper);
            var newNet = TradingIncomeAnalytics.CalculateNetSaleUnitPrice(newPrice);
            if (newNet < paid + lostFee)
            {
                continue;
            }

            picks.Add(new SellRelist(
                dearest,
                newPrice,
                $"listed {(int)age.TotalHours} h at {Coins(dearest.PriceCopper)}, undercut to {Coins(lowestAsk)}; " +
                $"relisting at {Coins(newPrice)} still nets {Coins(newNet)} vs {Coins(paid)} paid + {Coins(lostFee)} lost fee"));
        }

        return picks;
    }

    private static string Coins(long copper) => MarketScanItem.FormatCoins(copper);
}
