using Microsoft.Extensions.Logging;
using Mmopal.Contracts;
using Mmopal.Gw2;
using Mmopal.Infrastructure;

namespace Mmopal.Worker;

/// <summary>Measuring a session: time per step, what happened to each item, and the numbers saved after every cycle.</summary>
public sealed partial class WorkerRuntime
{
    private readonly SessionMetricsStore? _metricsStore;

    /// <summary>What one session has measured so far.</summary>
    private sealed class SessionMeasurements
    {
        public Dictionary<string, double> Seconds { get; } = new();
        public DateTimeOffset? StepStartedUtc { get; set; }
        public List<CycleMetrics> Cycles { get; } = [];
        public List<SessionEvent> Events { get; } = [];
        public int SavedEvents { get; set; }
        /// <summary>Items listed this session that only one character's bags held, by that character (see <see cref="InferCharacter"/>).</summary>
        public Dictionary<string, HashSet<string>> ListedFromOnlyHolder { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int ListedDuringEntry { get; set; }
        /// <summary>Items collected from the Delivery Box this session: their holder in the last bag read may be out of date.</summary>
        public HashSet<string> Collected { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Adds the time since the last mark to <paramref name="step"/>.</summary>
    private void MarkStep(TradingSession session, string step)
    {
        var now = _utcNow();
        var measured = session.Measured;
        if (measured.StepStartedUtc is { } started && now > started)
        {
            measured.Seconds[step] = measured.Seconds.GetValueOrDefault(step) + (now - started).TotalSeconds;
        }

        measured.StepStartedUtc = now;
    }

    /// <summary>Records an event and, when it says something to the player, shows it in the Activity Log.</summary>
    private void RecordEvent(
        TradingSession session,
        string kind,
        string outcome,
        string? item = null,
        long? unitCopper = null,
        string? detail = null,
        DateTimeOffset? at = null,
        string? activity = null)
    {
        session.Measured.Events.Add(new SessionEvent(
            at ?? _utcNow(),
            kind,
            outcome,
            item?.Trim(),
            session.LiveDriver?.CurrentCharacterName,
            unitCopper,
            detail));
        if (activity is not null)
        {
            Emit(new WorkerEvent(WorkerEventTypes.Log, activity, Timestamp: at ?? _utcNow()));
        }
    }

    private void RecordBuyEvent(TradingSession session, TradingActionResult result)
    {
        var action = result.Action;
        if (action.Kind != TradingActionKind.PlaceBuyOrder)
        {
            return;
        }

        var unit = result.PlacedUnitPriceCopper ?? action.PriceCopper;
        switch (result.Status)
        {
            case TradingActionStatus.Completed:
                RecordEvent(session, "buy", "placed", action.ItemName, unit, at: result.CompletedAt,
                    activity: $"Ordered {action.Quantity}× {action.ItemName} at {MarketScanItem.FormatCoins(unit)}");
                break;
            case TradingActionStatus.Simulated:
                RecordEvent(session, "buy", "simulated", action.ItemName, unit, at: result.CompletedAt);
                break;
            default:
                RecordEvent(session, "buy", SessionInsights.ReasonCode(result.Message), action.ItemName, unit, result.Message, result.CompletedAt,
                    result.Status == TradingActionStatus.Failed ? $"Did not order {action.ItemName}: {result.Message}" : null);
                break;
        }
    }

    private void RecordSellEvent(TradingSession session, SellPassItemResult result)
    {
        switch (result.Outcome)
        {
            case SellPassItemOutcome.Listed:
                var price = result.UnitCopper is { } unit ? $" at {MarketScanItem.FormatCoins(unit)}" : string.Empty;
                RecordEvent(session, "sell", "listed", result.ItemName, result.UnitCopper, activity: $"Listed {result.ItemName}{price}");
                InferCharacter(session, result.ItemName);
                break;
            case SellPassItemOutcome.Held:
                RecordEvent(session, "sell", "held", result.ItemName, detail: result.Details, activity: $"Holding {result.ItemName}: the price does not cover what was paid");
                break;
            case SellPassItemOutcome.NotFoundInBags:
                RecordEvent(session, "sell", "not-found", result.ItemName);
                break;
            default:
                RecordEvent(session, "sell", SessionInsights.ReasonCode(result.Details), result.ItemName, detail: result.Details, activity: $"Did not list {result.ItemName}: {result.Details}");
                break;
        }
    }

    /// <summary>
    /// When Character Select could not be read (every fresh launch on 22-23 Nov), the character is the one whose bags
    /// alone held two items that were just listed. Until it is known, stock on the other characters is searched for here
    /// and no swap goes to it.
    /// </summary>
    private void InferCharacter(TradingSession session, string listedItem)
    {
        if (session.LiveDriver is not { CurrentCharacterName: null } driver ||
            session.Measured.Collected.Contains(listedItem.Trim()) ||
            CharacterStock.OnlyHolder(listedItem.Trim(), session.FlipItemsByCharacter) is not { } holder)
        {
            return;
        }

        var listed = session.Measured.ListedFromOnlyHolder;
        if (session.Measured.ListedDuringEntry != driver.CharacterEntries)
        {
            session.Measured.ListedDuringEntry = driver.CharacterEntries;
            listed.Clear();
        }

        if (!listed.TryGetValue(holder, out var items))
        {
            listed[holder] = items = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        items.Add(listedItem.Trim());
        if (items.Count >= 2 && listed.Count == 1)
        {
            driver.AssumeCurrentCharacter(holder);
            _logger.LogInformation("Logged-in character is {Character}: only its bags held {Items}.", holder, string.Join(" and ", items));
        }
    }

    /// <summary>Saves the cycle's numbers and the events since the last save. Never lets a database problem stop trading.</summary>
    private async Task SaveCycleMetricsAsync(TradingSession session, TradingCycleResult result, CancellationToken cancellationToken)
    {
        var now = _utcNow();
        var measured = session.Measured;
        var orders = result.Market?.Transactions ?? [];
        var buys = orders.Where(order => order.Type == TransactionType.Buy).ToArray();
        var sells = orders.Where(order => order.Type == TransactionType.Sell).ToArray();
        static long Value(Transaction order) => Math.Max(0, order.PriceCopper) * Math.Max(1, order.Quantity);
        var cycle = new CycleMetrics(
            now,
            session.LiveDriver?.CurrentCharacterName,
            Gw2MarketCycle.Describe(now).PhaseName,
            measured.Seconds.ToDictionary(step => step.Key, step => Math.Round(step.Value, 1)),
            result.Decisions.Count,
            result.Decisions.Count(decision => decision.Action is not null),
            result.Decisions
                .Where(decision => decision.Action is null)
                .GroupBy(decision => TradingCycleController.SummarizeReason(decision.Reason))
                .ToDictionary(group => group.Key, group => group.Count()),
            result.Market?.Balance?.Copper,
            buys.Sum(Value),
            buys.Where(order => now - order.Created >= TimeSpan.FromHours(12)).Sum(Value),
            buys.Length,
            buys.Count(order => order.MinimumBuyPriceCopper > order.PriceCopper),
            (long)(sells.Sum(Value) * 0.85m),
            sells.Length,
            sells.Count(order => order.MinimumSellPriceCopper < order.PriceCopper),
            session.SellQueue.Count,
            session.FlipStockByCharacter.Sum(entry => entry.Items),
            session.BuysWaitingOnStock);
        measured.Seconds.Clear();
        measured.Cycles.Add(cycle);

        if (_metricsStore is null || session.Mode != RunMode.Live)
        {
            return;
        }

        try
        {
            await _metricsStore.SaveAsync(session.RunId, cycle, measured.Events.Skip(measured.SavedEvents).ToArray(), cancellationToken).ConfigureAwait(false);
            measured.SavedEvents = measured.Events.Count;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not save this cycle's measurements.");
        }
    }

    /// <summary>Saves events recorded after the last full cycle and adds the findings to the report.</summary>
    private async Task<TradingSessionReport> AddMeasurementsAsync(TradingSession session, TradingSessionReport report)
    {
        var measured = session.Measured;
        if (_metricsStore is not null && session.Mode == RunMode.Live && measured.SavedEvents < measured.Events.Count)
        {
            try
            {
                await _metricsStore.SaveAsync(session.RunId, null, measured.Events.Skip(measured.SavedEvents).ToArray()).ConfigureAwait(false);
                measured.SavedEvents = measured.Events.Count;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not save the last events of the session.");
            }
        }

        return report with
        {
            SecondsByStep = SessionInsights.SecondsByStep(measured.Cycles),
            OutcomeCounts = SessionInsights.OutcomeCounts(measured.Events),
            Findings = SessionInsights.Findings(measured.Cycles, measured.Events)
        };
    }
}
