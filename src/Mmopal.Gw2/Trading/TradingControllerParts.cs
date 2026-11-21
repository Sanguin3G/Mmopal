using System.Collections.Concurrent;
using Mmopal.Contracts;

namespace Mmopal.Gw2;

// Market readers and action executors the cycle controller plugs into, plus its options and results.
public interface IGw2MarketReader
{
    Task<MarketSnapshot> ReadAsync(CancellationToken cancellationToken = default);
}

public interface ITradingActionExecutor
{
    Task<TradingActionResult> ExecuteAsync(TradingAction action, CancellationToken cancellationToken = default);
}

public sealed class Gw2DataMarketReader : IGw2MarketReader
{
    private readonly IGw2DataProvider _provider;

    public Gw2DataMarketReader(IGw2DataProvider provider) => _provider = provider;

    public async Task<MarketSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        var transactions = await _provider.GetTransactionsAsync(cancellationToken);
        var balance = await _provider.GetBalanceAsync(cancellationToken);
        return new MarketSnapshot(transactions, balance, DateTimeOffset.UtcNow, "gw2-api");
    }
}

public sealed class ReplayMarketReader : IGw2MarketReader
{
    private readonly MarketSnapshot _snapshot;
    private readonly bool _rebaseTimestamp;

    public ReplayMarketReader(MarketSnapshot snapshot, bool rebaseTimestamp = true)
    {
        _snapshot = snapshot;
        _rebaseTimestamp = rebaseTimestamp;
    }

    public Task<MarketSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = _rebaseTimestamp
            ? _snapshot with { ObservedAt = DateTimeOffset.UtcNow }
            : _snapshot;
        return Task.FromResult(snapshot);
    }
}

public sealed class EmptyMarketReader : IGw2MarketReader
{
    public Task<MarketSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new MarketSnapshot(
            Array.Empty<Transaction>(),
            null,
            DateTimeOffset.UtcNow,
            "empty-replay"));
    }
}

public sealed class DryRunActionExecutor : ITradingActionExecutor
{
    private readonly ConcurrentQueue<TradingAction> _actions = new();

    public IReadOnlyCollection<TradingAction> Actions => _actions.ToArray();

    public Task<TradingActionResult> ExecuteAsync(TradingAction action, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _actions.Enqueue(action);
        return Task.FromResult(new TradingActionResult(
            action,
            TradingActionStatus.Simulated,
            "DryRun recorded the action; no game input was issued."));
    }
}

public sealed class ObserveActionExecutor : ITradingActionExecutor
{
    public Task<TradingActionResult> ExecuteAsync(TradingAction action, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new TradingActionResult(
            action,
            TradingActionStatus.Observed,
            "Observe mode reported the action; no game input was issued."));
    }
}

public enum TradingActionStatus
{
    Simulated,
    Observed,
    Completed,
    Failed,
    Skipped
}

public sealed record TradingActionResult(
    TradingAction Action,
    TradingActionStatus Status,
    string Message,
    DateTimeOffset CompletedAt)
{
    /// <summary>Unit price actually typed into the order dialog (Type exact price mode), for the API audit.</summary>
    public long? PlacedUnitPriceCopper { get; init; }

    public TradingActionResult(TradingAction action, TradingActionStatus status, string message)
        : this(action, status, message, DateTimeOffset.UtcNow) { }
}

public sealed class TradingControllerOptions
{
    public int MaxRetries { get; init; } = 3;
    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan MaxMarketAge { get; init; } = TimeSpan.FromMinutes(5);
    public long MinimumBalanceCopper { get; init; }
    public int MaxActionsPerCycle { get; init; } = 3;

    /// <summary>
    /// Most of total gold (wallet + buy orders + listings after fees) that may sit in buy orders; 0 = no limit. In a
    /// 7 h session 60% sat in buy orders while a third filled, leaving no gold for items that do fill.
    /// </summary>
    public int MaxBuyOrderSharePercent { get; init; }

    /// <summary>
    /// How long an item is held back after an order was placed for it. This only has to outlast the
    /// GW2 API cache (~5 min) so a just-placed order that hasn't reached <c>current/buys</c> yet isn't placed twice.
    /// </summary>
    public TimeSpan RecentActionCooldown { get; init; } = TimeSpan.FromMinutes(6);

    public Func<DateTimeOffset> UtcNow { get; init; } = () => DateTimeOffset.UtcNow;
}

public sealed record TradingCycleResult(
    TradingState FinalState,
    bool Succeeded,
    MarketSnapshot? Market,
    IReadOnlyList<CandidateDecision> Decisions,
    IReadOnlyList<TradingActionResult> Actions,
    int MarketReadAttempts,
    string? Error);
