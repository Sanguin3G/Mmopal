using Mmopal.Contracts;

namespace Mmopal.Gw2;

/// <summary>
/// "Bot war" guard. Some items are camped by other bots that re-outbid every few minutes: in one 7 h session
/// Super Sharpening Polygon got 12 buy orders, 11 outbid cancels and 0 fills, and ~100 orders went to items that never
/// filled. After <see cref="OutbidCancelsBeforePause"/> outbid cancels without a single fill, the item is paused for
/// <see cref="PauseDuration"/> so those order slots go to items that actually fill. A fill resets the count.
/// </summary>
public sealed class BuyCompetitionTracker
{
    public const int OutbidCancelsBeforePause = 3;
    public static readonly TimeSpan PauseDuration = TimeSpan.FromHours(3);

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
                _paused[key] = (name, nowUtc + PauseDuration);
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

    public void RecordFills(IEnumerable<string> itemNames)
    {
        ArgumentNullException.ThrowIfNull(itemNames);
        foreach (var raw in itemNames)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var key = ItemNameNormalizer.Normalize(raw);
            _cancelsSinceFill.Remove(key);
            _paused.Remove(key);
        }
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
/// is deliberately narrow: listed 12 h+, undercut, an item that actually trades (on the scanner's shopping list),
/// relisted at most once a day, and only when the new price still covers what was paid plus the lost listing fee
/// after the new 5% + 10% fees. Anything else waits: a listing that sits costs nothing more.
/// </summary>
public static class SellRelistPolicy
{
    public static readonly TimeSpan MinListingAge = TimeSpan.FromHours(12);
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
            var oldest = group.OrderBy(order => order.Created).First();
            var age = nowUtc - oldest.Created;
            if (group.Any(order => order.MinimumSellPriceCopper is not { } ask || ask <= 0 || ask >= order.PriceCopper))
            {
                continue; // one of our listings is at (or is) the lowest ask, or the market is unknown: it can still sell
            }

            if (age < MinListingAge ||
                !tradedItems.Contains(name) ||
                !paidByItem.TryGetValue(name, out var paid) || paid <= 0 ||
                (lastRelistedUtc.TryGetValue(name, out var last) && nowUtc - last < MinTimeBetweenRelists))
            {
                continue;
            }

            var lowestAsk = oldest.MinimumSellPriceCopper!.Value;
            var newPrice = Math.Max(1, lowestAsk - Math.Max(0, undercutCopper));
            var lostFee = TradingIncomeAnalytics.CalculateListingFee(oldest.PriceCopper);
            var newNet = TradingIncomeAnalytics.CalculateNetSaleUnitPrice(newPrice);
            if (newNet < paid + lostFee)
            {
                continue;
            }

            picks.Add(new SellRelist(
                oldest,
                newPrice,
                $"listed {(int)age.TotalHours} h at {Coins(oldest.PriceCopper)}, undercut to {Coins(lowestAsk)}; " +
                $"relisting at {Coins(newPrice)} still nets {Coins(newNet)} vs {Coins(paid)} paid + {Coins(lostFee)} lost fee"));
        }

        return picks;
    }

    private static string Coins(long copper) => MarketScanItem.FormatCoins(copper);
}
