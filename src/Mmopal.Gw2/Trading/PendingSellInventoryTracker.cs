namespace Mmopal.Gw2;

public enum SellPassItemOutcome
{
    Listed,
    NotFoundInBags,
    SkippedError,
    // Listing now would be below break-even; checked again later.
    Held
}

/// <summary>
/// What the worker knows about a sell target before the UI opens: the API lowest ask (resolves the ambiguous
/// Current Sellers row text and is the fallback price) and the lowest listing price that still covers what Mmopal
/// paid after the 15% fees.
/// </summary>
/// <param name="OwnLowestListingCopper">
/// The lowest price of Mmopal's own current listings of the item, if any: when the lowest ask is this listing, a new
/// unit joins it at the same price instead of undercutting it.
/// </param>
public sealed record SellPricing(long? ApiLowestAskCopper, long? BreakEvenCopper, long? OwnLowestListingCopper = null)
{
    /// <summary>Lowest listing price whose net after the 5% listing and 10% exchange fees covers the cost.</summary>
    public static long BreakEvenListingPrice(long costCopper)
    {
        if (costCopper <= 0)
        {
            return 1;
        }

        var engine = new ProfitabilityEngine();
        var policy = new ProfitabilityPolicy();
        var price = (long)Math.Ceiling(costCopper / 0.85m);
        for (var guard = 0; guard < 10_000 && engine.Evaluate(costCopper, price, policy).ProfitCopper < 0; guard++)
        {
            price++;
        }

        return price;
    }
}

public sealed record SellPassItemResult(
    string ItemName,
    SellPassItemOutcome Outcome,
    string? Details = null,
    long? UnitCopper = null);

/// <summary>
/// Tracks flip items confirmed to be in the player's bags or Delivery Box pickups so the worker
/// never opens the Sell tab to blindly type unbought or still-open buy-order item names.
/// </summary>
public sealed class PendingSellInventoryTracker
{
    private static readonly TimeSpan NotFoundRetryBackoff = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ErrorRetryBackoff = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan HeldRetryBackoff = TimeSpan.FromMinutes(30);
    // Bags belong to one character: a stack missed twice is on another character, so it waits for a character change
    // (CharacterChanged) instead of being re-checked every 15 min (124 wasted searches in one 7 h session).
    private static readonly TimeSpan DoubleMissSuppression = TimeSpan.FromHours(3);

    private readonly Dictionary<string, PendingEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _suppressedUntilUtc = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, DateTimeOffset> _listedAtUtc = new(StringComparer.OrdinalIgnoreCase);

    // The bags and listings the API reports are up to ~5 min old: a just-listed item still shows in the bags, and an item
    // in no bag this long after pickup is not there any more. The 22-23 Nov run searched 146 times for such items.
    // The bags and the trade history the API reports trail the game by up to ~10 minutes: with orders of 4 filling one unit
    // at a time, a listed unit came back as "to list" three times in 20 minutes (Superior Sigil of Celerity, 26 Nov).
    private static readonly TimeSpan ApiLag = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan UnseenDropAge = TimeSpan.FromMinutes(10);

    public int Count => _entries.Count;

    /// <summary>True when Mmopal listed the item so recently that the API may not show that listing yet.</summary>
    public bool WasJustListed(string itemName, DateTimeOffset nowUtc) =>
        _listedAtUtc.TryGetValue(itemName.Trim(), out var listedAt) && nowUtc - listedAt < ApiLag;

    /// <summary>Drops queued items that no character's bags have shown for a while (sold, deposited or moved by hand).</summary>
    public void DropUnseen(IEnumerable<string> itemsInBags, DateTimeOffset nowUtc)
    {
        var seen = itemsInBags.Select(item => item.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in _entries.Values.Where(entry => !seen.Contains(entry.ItemName) && nowUtc - entry.LastConfirmedUtc >= UnseenDropAge).ToArray())
        {
            _entries.Remove(entry.ItemName);
        }
    }

    /// <summary>After a character swap or game restart the bags are different: re-check everything.</summary>
    public void CharacterChanged(DateTimeOffset nowUtc)
    {
        _suppressedUntilUtc.Clear();
        foreach (var entry in _entries.Values)
        {
            entry.MissCount = 0;
            if (entry.NextEligibleAttemptUtc > nowUtc)
            {
                entry.NextEligibleAttemptUtc = nowUtc;
            }
        }
    }

    public void RecordDeliveryPickup(IEnumerable<string> itemNames, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(itemNames);
        foreach (var raw in itemNames)
        {
            UpsertImmediate(raw, nowUtc);
        }
    }

    public void RecordNewBuyFill(IEnumerable<string> filledFlipItemNames, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(filledFlipItemNames);
        foreach (var raw in filledFlipItemNames)
        {
            UpsertImmediate(raw, nowUtc);
        }
    }

    public void SyncFromCharacterInventory(IEnumerable<string> unboundOwnedFlipItemNames, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(unboundOwnedFlipItemNames);
        foreach (var raw in unboundOwnedFlipItemNames)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var name = raw.Trim();
            if (!_entries.ContainsKey(name) && WasJustListed(name, nowUtc))
            {
                continue; // the bags the API reports are from before the listing
            }

            if (_suppressedUntilUtc.TryGetValue(name, out var suppressedUntil))
            {
                if (nowUtc < suppressedUntil)
                {
                    continue;
                }

                _suppressedUntilUtc.Remove(name);
            }

            if (_entries.TryGetValue(name, out var existing))
            {
                // Preserve retry backoff if an earlier check noticed the stack was on another character.
                if (existing.MissCount == 0)
                {
                    existing.LastConfirmedUtc = nowUtc;
                }
            }
            else
            {
                _entries[name] = new PendingEntry(name, nowUtc, nowUtc);
            }
        }
    }

    public IReadOnlyList<string> GetReadyTargets(DateTimeOffset nowUtc, int maxPerPass)
    {
        if (maxPerPass <= 0 || _entries.Count == 0)
        {
            return Array.Empty<string>();
        }

        return _entries.Values
            .Where(entry => entry.NextEligibleAttemptUtc <= nowUtc)
            .OrderByDescending(entry => entry.LastConfirmedUtc)
            .ThenBy(entry => entry.ItemName, StringComparer.OrdinalIgnoreCase)
            .Take(maxPerPass)
            .Select(entry => entry.ItemName)
            .ToArray();
    }

    public void RecordSellPassResults(IEnumerable<SellPassItemResult> results, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(results);
        foreach (var result in results)
        {
            if (string.IsNullOrWhiteSpace(result.ItemName))
            {
                continue;
            }

            var name = result.ItemName.Trim();
            switch (result.Outcome)
            {
                case SellPassItemOutcome.Listed:
                    _entries.Remove(name);
                    _suppressedUntilUtc.Remove(name);
                    _listedAtUtc[name] = nowUtc;
                    break;

                case SellPassItemOutcome.NotFoundInBags:
                    RecordMiss(name, nowUtc);
                    break;

                case SellPassItemOutcome.SkippedError:
                    // 2, 4, 8, 16, then every 30 min: an item whose window cannot be used was tried every cycle (24 Nov).
                    var errors = _entries.TryGetValue(name, out var failing) ? failing.ErrorCount++ : 0;
                    DelayNextAttempt(name, nowUtc + TimeSpan.FromTicks(Math.Min(HeldRetryBackoff.Ticks, ErrorRetryBackoff.Ticks << Math.Min(errors, 4))));
                    break;

                case SellPassItemOutcome.Held:
                    DelayNextAttempt(name, nowUtc + HeldRetryBackoff);
                    break;
            }
        }
    }

    private void RecordMiss(string name, DateTimeOffset nowUtc)
    {
        if (!_entries.TryGetValue(name, out var entry))
        {
            return;
        }

        entry.MissCount++;
        if (entry.MissCount == 1)
        {
            entry.NextEligibleAttemptUtc = nowUtc + NotFoundRetryBackoff;
            return;
        }

        _entries.Remove(name);
        _suppressedUntilUtc[name] = nowUtc + DoubleMissSuppression;
    }

    private void DelayNextAttempt(string name, DateTimeOffset nextAttemptUtc)
    {
        if (_entries.TryGetValue(name, out var entry))
        {
            entry.NextEligibleAttemptUtc = nextAttemptUtc;
        }
    }

    private void UpsertImmediate(string raw, DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return;
        }

        var name = raw.Trim();
        _suppressedUntilUtc.Remove(name);
        // Just listed: the report may be of the unit that went up. Tried once the bags can tell.
        var when = _listedAtUtc.TryGetValue(name, out var listedAt) && nowUtc - listedAt < ApiLag ? listedAt + ApiLag : nowUtc;
        if (_entries.TryGetValue(name, out var existing))
        {
            existing.MissCount = 0;
            existing.LastConfirmedUtc = nowUtc;
            existing.NextEligibleAttemptUtc = when;
        }
        else
        {
            _entries[name] = new PendingEntry(name, nowUtc, when);
        }
    }

    private sealed class PendingEntry(string itemName, DateTimeOffset lastConfirmedUtc, DateTimeOffset nextEligibleAttemptUtc)
    {
        public string ItemName { get; } = itemName;
        public DateTimeOffset LastConfirmedUtc { get; set; } = lastConfirmedUtc;
        public DateTimeOffset NextEligibleAttemptUtc { get; set; } = nextEligibleAttemptUtc;
        public int MissCount { get; set; }
        public int ErrorCount { get; set; }
    }
}
