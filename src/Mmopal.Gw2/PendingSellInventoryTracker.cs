namespace Mmopal.Gw2;

public enum SellPassItemOutcome
{
    Listed,
    NotFoundInBags,
    SkippedError
}

public sealed record SellPassItemResult(
    string ItemName,
    SellPassItemOutcome Outcome,
    string? Details = null);

/// <summary>
/// Tracks flip items confirmed to be in the player's bags or Delivery Box pickups so the worker
/// never opens the Sell tab to blindly type unbought or still-open buy-order item names.
/// </summary>
public sealed class PendingSellInventoryTracker
{
    private static readonly TimeSpan NotFoundRetryBackoff = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ErrorRetryBackoff = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan DoubleMissSuppression = TimeSpan.FromMinutes(15);

    private readonly Dictionary<string, PendingEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _suppressedUntilUtc = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _entries.Count;

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
                    break;

                case SellPassItemOutcome.NotFoundInBags:
                    if (!_entries.TryGetValue(name, out var entry))
                    {
                        break;
                    }

                    entry.MissCount++;
                    if (entry.MissCount == 1)
                    {
                        entry.NextEligibleAttemptUtc = nowUtc + NotFoundRetryBackoff;
                    }
                    else
                    {
                        _entries.Remove(name);
                        _suppressedUntilUtc[name] = nowUtc + DoubleMissSuppression;
                    }

                    break;

                case SellPassItemOutcome.SkippedError:
                    if (_entries.TryGetValue(name, out var errEntry))
                    {
                        errEntry.NextEligibleAttemptUtc = nowUtc + ErrorRetryBackoff;
                    }

                    break;
            }
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
        if (_entries.TryGetValue(name, out var existing))
        {
            existing.MissCount = 0;
            existing.LastConfirmedUtc = nowUtc;
            existing.NextEligibleAttemptUtc = nowUtc;
        }
        else
        {
            _entries[name] = new PendingEntry(name, nowUtc, nowUtc);
        }
    }

    private sealed class PendingEntry(string itemName, DateTimeOffset lastConfirmedUtc, DateTimeOffset nextEligibleAttemptUtc)
    {
        public string ItemName { get; } = itemName;
        public DateTimeOffset LastConfirmedUtc { get; set; } = lastConfirmedUtc;
        public DateTimeOffset NextEligibleAttemptUtc { get; set; } = nextEligibleAttemptUtc;
        public int MissCount { get; set; }
    }
}
