using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Mmopal.Contracts;
using Mmopal.Infrastructure;

namespace Mmopal.Gw2;

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

public sealed class TradingCycleController
{
    private readonly CheckpointStore _store;
    private readonly ILogger _logger;
    private readonly IGw2MarketReader _marketReader;
    private readonly ITradingActionExecutor _actionExecutor;
    private readonly CandidatePlanner _candidatePlanner;
    private readonly TradingControllerOptions _options;
    // Keyed by run + action kind + item (not price): the live executor bids off the in-game order book,
    // so a 1c move in the API quote must not let the same item be ordered twice while current/buys lags.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _recentActions = new(StringComparer.Ordinal);

    // Live buys pick the first Trading Post search result, so "Slice of Allspice Cake" can open
    // "Slice of Allspice Cake with Ice Cream". Items whose search proved to land on another item are
    // remembered per run (item key -> the item that was actually ordered) and no longer bought.
    private readonly ConcurrentDictionary<string, string> _boughtItems = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _searchMismatches = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _blockedUntil = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (string Name, long Price, DateTimeOffset PlacedAt)> _placedPrices = new(StringComparer.Ordinal);
    private static readonly TimeSpan FailedItemCooldown = TimeSpan.FromMinutes(30);

    private static bool IsItemSpecificBuyFailure(string? message) =>
        message is not null &&
        (message.StartsWith("Buy order aborted", StringComparison.Ordinal) ||
         message.StartsWith("None of the search rows opened", StringComparison.Ordinal));

    public TradingCycleController(
        CheckpointStore store,
        ILogger logger,
        IGw2MarketReader marketReader,
        ITradingActionExecutor actionExecutor,
        TradingControllerOptions? options = null,
        CandidatePlanner? candidatePlanner = null)
    {
        _store = store;
        _logger = logger;
        _marketReader = marketReader;
        _actionExecutor = actionExecutor;
        _options = options ?? new TradingControllerOptions();
        _candidatePlanner = candidatePlanner ?? new CandidatePlanner();
    }

    public async Task<TradingCycleResult> RunCycleAsync(
        string runId,
        IReadOnlyList<ItemCandidate> candidates,
        IReadOnlyList<string> blacklist,
        ProfitabilityPolicy policy,
        CancellationToken cancellationToken = default,
        Func<bool>? isPaused = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(blacklist);

        var machine = new TradingStateMachine();
        machine.Start();
        IReadOnlyList<CandidateDecision> decisions = Array.Empty<CandidateDecision>();
        var actionResults = new List<TradingActionResult>();
        MarketSnapshot? market = null;
        var marketAttempts = 0;

        try
        {
            machine.TransitionTo(TradingState.RefreshMarket);
            (market, marketAttempts) = await ReadMarketWithRetryAsync(cancellationToken);
            if (!IsFresh(market.ObservedAt))
            {
                machine.TransitionTo(TradingState.Recovering);
                return await FinishAsync(
                    runId,
                    machine.State,
                    false,
                    market,
                    decisions,
                    actionResults,
                    marketAttempts,
                    $"Market snapshot is stale; observed at {market.ObservedAt:O}.",
                    cancellationToken);
            }

            machine.TransitionTo(TradingState.ReconcileOrders);
            var transactionIndex = new TransactionIndex(market.Transactions);
            var blacklistMatcher = new BlacklistMatcher(blacklist);
            machine.TransitionTo(TradingState.ProcessCandidates);
            decisions = _candidatePlanner.Evaluate(candidates, transactionIndex, blacklistMatcher, policy).ToArray();

            if (market.Balance is null)
            {
                decisions = decisions
                    .Select(decision => decision.Action?.Kind != TradingActionKind.PlaceBuyOrder
                        ? decision
                        : CandidateDecision.Skip(decision.Candidate, "Balance is unavailable; no action is safe.", decision.Profitability))
                    .ToArray();
            }
            else if (market.Balance.Copper <= 0)
            {
                decisions = decisions
                    .Select(decision => decision.Action?.Kind != TradingActionKind.PlaceBuyOrder
                        ? decision
                        : CandidateDecision.Skip(decision.Candidate, "Wallet balance is 0g; buy orders paused (sell/collect only).", decision.Profitability))
                    .ToArray();
            }
            else if (_options.MinimumBalanceCopper > 0 && market.Balance.Copper <= _options.MinimumBalanceCopper)
            {
                decisions = decisions
                    .Select(decision => decision.Action?.Kind != TradingActionKind.PlaceBuyOrder
                        ? decision
                        : CandidateDecision.Skip(decision.Candidate, "Balance is at or below the configured gold reserve floor; buy orders paused (sell/collect only).", decision.Profitability))
                    .ToArray();
            }
            long? remainingSpendable = market.Balance is { Copper: > 0 } balance
                ? Math.Max(0L, balance.Copper - Math.Max(0L, _options.MinimumBalanceCopper))
                : null;
            DetectSearchMismatches(runId, market.Transactions);
            AuditPlacedPrices(runId, market.Transactions);
            decisions = SelectActionsForCycle(runId, decisions, remainingSpendable);
            foreach (var decision in decisions)
            {
                // ~150 skip lines per cycle buried the log; skips go to Debug and are summarized below.
                _logger.Log(
                    decision.Action is null ? LogLevel.Debug : LogLevel.Information,
                    "GW2 decision {Outcome} for {Item}: {Reason}",
                    decision.Kind,
                    decision.Candidate.ItemName,
                    decision.Reason);
            }

            var skippedSummary = decisions
                .Where(decision => decision.Action is null)
                .GroupBy(decision => SummarizeReason(decision.Reason))
                .OrderByDescending(group => group.Count())
                .Take(4)
                .Select(group => $"{group.Count()} {group.Key}")
                .ToArray();
            _logger.LogInformation(
                "GW2 cycle plan: {Accepted} accepted, {Skipped} skipped ({Reasons}).",
                decisions.Count(decision => decision.Action is not null),
                decisions.Count(decision => decision.Action is null),
                skippedSummary.Length == 0 ? "none" : string.Join("; ", skippedSummary));

            machine.TransitionTo(TradingState.Checkpoint);
            await _store.SaveCheckpointAsync(
                runId,
                machine.State.ToString(),
                new
                {
                    market.Source,
                    market.ObservedAt,
                    candidateCount = candidates.Count,
                    acceptedCount = decisions.Count(decision => decision.Action is not null),
                    skippedCount = decisions.Count(decision => decision.Action is null)
                },
                cancellationToken);

            foreach (var decision in decisions.Where(decision => decision.Action is not null))
            {
                var action = decision.Action!;
                if (isPaused?.Invoke() == true)
                {
                    _logger.LogInformation("Trading cycle paused before executing {Kind} for {Item}.", action.Kind, action.ItemName);
                    break;
                }

                var actionId = GetActionId(runId, action, _options.UtcNow());
                await _store.SaveActionAsync(actionId, runId, action.Kind.ToString(), action.ItemName, "Planned", action, cancellationToken: cancellationToken);
                TradingActionResult result;
                try
                {
                    result = await _actionExecutor.ExecuteAsync(action, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    var failed = new TradingActionResult(action, TradingActionStatus.Failed, exception.Message);
                    actionResults.Add(failed);
                    await _store.SaveActionAsync(actionId, runId, action.Kind.ToString(), action.ItemName, failed.Status.ToString(), failed, cancellationToken: cancellationToken);
                    machine.TransitionTo(TradingState.Recovering);
                    return await FinishAsync(runId, machine.State, false, market, decisions, actionResults, marketAttempts, exception.Message, cancellationToken);
                }

                if (result.Status is TradingActionStatus.Simulated or TradingActionStatus.Observed or TradingActionStatus.Completed)
                {
                    _recentActions[GetCooldownKey(runId, action)] = _options.UtcNow();
                }

                if (result.Status == TradingActionStatus.Completed && action.Kind == TradingActionKind.PlaceBuyOrder)
                {
                    _boughtItems[GetItemKey(runId, action.ItemName)] = action.ItemName.Trim();
                    if (result.PlacedUnitPriceCopper is { } placedPrice)
                    {
                        _placedPrices[GetItemKey(runId, action.ItemName)] = (action.ItemName.Trim(), placedPrice, _options.UtcNow());
                    }
                }

                if (result.Status == TradingActionStatus.Failed && IsItemSpecificBuyFailure(result.Message))
                {
                    // The same dialog shows the same wrong price / wrong item next cycle too (Pyre Dye was aborted
                    // four cycles in a row); give the slot to other items for a while.
                    _blockedUntil[GetItemKey(runId, action.ItemName)] = _options.UtcNow() + FailedItemCooldown;
                }

                if (result.Status == TradingActionStatus.Failed)
                {
                    _logger.LogWarning(
                        "GW2 action {Kind} {Status} for {Item}: {Message}",
                        action.Kind,
                        result.Status,
                        action.ItemName,
                        result.Message);
                }
                else
                {
                    _logger.LogInformation(
                        "GW2 action {Kind} {Status} for {Item}: {Message}",
                        action.Kind,
                        result.Status,
                        action.ItemName,
                        result.Message);
                }

                actionResults.Add(result);
                try
                {
                    await _store.SaveActionAsync(actionId, runId, action.Kind.ToString(), action.ItemName, result.Status.ToString(), result, cancellationToken: cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _logger.LogError(exception, "Failed to persist action {ActionId} after execution; keeping in-memory guard to prevent duplicate orders.", actionId);
                    machine.TransitionTo(TradingState.Recovering);
                    return new TradingCycleResult(
                        machine.State,
                        false,
                        market,
                        decisions,
                        actionResults,
                        marketAttempts,
                        $"Action executed but checkpoint save failed: {exception.Message}");
                }
            }

            machine.TransitionTo(TradingState.Waiting);
            return await FinishAsync(runId, machine.State, true, market, decisions, actionResults, marketAttempts, null, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            machine.Stop();
            throw;
        }
        catch (Exception exception)
        {
            machine.TransitionTo(TradingState.Faulted);
            _logger.LogError(exception, "GW2 trading cycle failed.");
            return await FinishAsync(runId, machine.State, false, market, decisions, actionResults, marketAttempts, exception.Message, cancellationToken);
        }
    }

    private async Task<(MarketSnapshot Market, int Attempts)> ReadMarketWithRetryAsync(CancellationToken cancellationToken)
    {
        var attempts = 0;
        while (true)
        {
            attempts++;
            try
            {
                return (await _marketReader.ReadAsync(cancellationToken), attempts);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (attempts <= _options.MaxRetries)
            {
                _logger.LogWarning(exception, "GW2 market read failed on attempt {Attempt}; retrying.", attempts);
                await DelayBeforeRetryAsync(attempts, cancellationToken);
            }
        }
    }

    private async Task DelayBeforeRetryAsync(int attempt, CancellationToken cancellationToken)
    {
        var multiplier = Math.Pow(2, Math.Max(0, attempt - 1));
        var delay = TimeSpan.FromMilliseconds(Math.Min(_options.RetryBaseDelay.TotalMilliseconds * multiplier, 10_000));
        await Task.Delay(delay, cancellationToken);
    }

    /// <summary>
    /// Walks the ranked decisions once, in order: items still on cooldown are skipped first, then the
    /// per-cycle cap and the gold budget only count actions that will actually run. Doing the cooldown
    /// check before the cap keeps recently-ordered items from occupying all slots and starving the list.
    /// </summary>
    private IReadOnlyList<CandidateDecision> SelectActionsForCycle(
        string runId,
        IReadOnlyList<CandidateDecision> decisions,
        long? remainingSpendable)
    {
        var now = _options.UtcNow();
        var accepted = 0;
        var selected = new List<CandidateDecision>(decisions.Count);
        foreach (var decision in decisions)
        {
            if (decision.Action is not { } action)
            {
                selected.Add(decision);
                continue;
            }

            if (action.Kind == TradingActionKind.PlaceBuyOrder &&
                _searchMismatches.TryGetValue(GetItemKey(runId, action.ItemName), out var orderedInstead))
            {
                selected.Add(CandidateDecision.Skip(
                    decision.Candidate,
                    $"Trading Post search for '{action.ItemName}' opens '{orderedInstead}' first (that is the order that appeared); skipped for this run so the wrong item is not bought again.",
                    decision.Profitability));
                continue;
            }

            if (_blockedUntil.TryGetValue(GetItemKey(runId, action.ItemName), out var blockedUntil) && blockedUntil > now)
            {
                selected.Add(CandidateDecision.Skip(
                    decision.Candidate,
                    $"Last buy attempt was aborted (wrong price or item in the dialog); retrying after {(int)Math.Ceiling((blockedUntil - now).TotalMinutes)} min.",
                    decision.Profitability));
                continue;
            }

            var key = GetCooldownKey(runId, action);
            if (_recentActions.TryGetValue(key, out var placedAt))
            {
                var remaining = placedAt + _options.RecentActionCooldown - now;
                if (remaining > TimeSpan.Zero)
                {
                    selected.Add(CandidateDecision.Skip(
                        decision.Candidate,
                        $"Order placed recently, waiting for the GW2 API to show it (placed {(int)(now - placedAt).TotalSeconds}s ago, {(int)Math.Ceiling(remaining.TotalSeconds)}s left).",
                        decision.Profitability));
                    continue;
                }

                _recentActions.TryRemove(key, out _);
            }

            if (accepted >= _options.MaxActionsPerCycle)
            {
                selected.Add(CandidateDecision.Skip(decision.Candidate, "Per-cycle action limit reached.", decision.Profitability));
                continue;
            }

            if (action.Kind == TradingActionKind.PlaceBuyOrder && remainingSpendable is { } spendable)
            {
                var rawCost = (decimal)action.PriceCopper * Math.Max(1L, action.Quantity);
                if (rawCost > spendable)
                {
                    var needed = rawCost > long.MaxValue ? long.MaxValue : (long)rawCost;
                    selected.Add(CandidateDecision.Skip(
                        decision.Candidate,
                        $"Insufficient spendable gold ({MarketScanItem.FormatCoins(spendable)} available above floor; order needs {MarketScanItem.FormatCoins(needed)}).",
                        decision.Profitability));
                    continue;
                }

                remainingSpendable = spendable - (long)rawCost;
            }

            accepted++;
            selected.Add(decision);
        }

        return selected;
    }

    private bool IsFresh(DateTimeOffset observedAt)
    {
        var now = DateTimeOffset.UtcNow;
        return observedAt <= now.AddSeconds(5) && now - observedAt <= _options.MaxMarketAge;
    }

    private async Task<TradingCycleResult> FinishAsync(
        string runId,
        TradingState state,
        bool succeeded,
        MarketSnapshot? market,
        IReadOnlyList<CandidateDecision> decisions,
        IReadOnlyList<TradingActionResult> actions,
        int marketAttempts,
        string? error,
        CancellationToken cancellationToken)
    {
        await _store.SaveCheckpointAsync(
            runId,
            state.ToString(),
            new
            {
                succeeded,
                error,
                decisions = decisions.Select(decision => new
                {
                    item = decision.Candidate.ItemName,
                    outcome = decision.Kind.ToString(),
                    decision.Reason,
                    action = decision.Action?.Signature
                }),
                actions = actions.Select(action => new { action = action.Action.Signature, status = action.Status.ToString(), action.Message }),
                completedAt = DateTimeOffset.UtcNow
            },
            cancellationToken);
        return new TradingCycleResult(state, succeeded, market, decisions, actions, marketAttempts, error);
    }

    // Unique per attempt so re-ordering the same item later in a run keeps its own audit row.
    private static string GetActionId(string runId, TradingAction action, DateTimeOffset attemptedAt)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{runId}|{action.Signature}|{attemptedAt.UtcTicks}"));
        return "gw2-" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Flags items we bought this run that never showed up under their own name in current/buys while an
    /// order for a longer name containing it did — i.e. the search landed on a different item.
    /// </summary>
    private void DetectSearchMismatches(string runId, IReadOnlyList<Transaction> transactions)
    {
        var prefix = runId + "|";
        var buyNames = transactions
            .Where(tx => tx.Type == TransactionType.Buy && !string.IsNullOrWhiteSpace(tx.ItemName))
            .Select(tx => tx.ItemName.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var (key, name) in _boughtItems)
        {
            if (!key.StartsWith(prefix, StringComparison.Ordinal) ||
                _searchMismatches.ContainsKey(key) ||
                buyNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var other = buyNames.FirstOrDefault(buy => buy.Contains(name, StringComparison.OrdinalIgnoreCase));
            if (other is not null && _searchMismatches.TryAdd(key, other))
            {
                _logger.LogWarning(
                    "Bought '{Item}' but current/buys shows an order for '{Other}' instead: the Trading Post search opens the wrong item first. Skipping it for the rest of this run; consider blacklisting it.",
                    name,
                    other);
            }
        }
    }

    /// <summary>Strips per-item details (amounts, names in quotes) so skip reasons group into a short summary.</summary>
    private static string SummarizeReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return "no reason";
        }

        var cut = reason.IndexOfAny(['(', ';', '\'']);
        var head = (cut > 0 ? reason[..cut] : reason).Trim().TrimEnd('.', ':', ',').Trim();
        return head.Length > 60 ? head[..60] + "…" : head;
    }

    /// <summary>
    /// Typed-price audit through the API: once current/buys lists the order (it lags up to ~5 minutes), its price
    /// must equal what was typed. A mismatch is logged as a warning; an order that never shows up (filled or
    /// cancelled meanwhile) is dropped quietly.
    /// </summary>
    private void AuditPlacedPrices(string runId, IReadOnlyList<Transaction> transactions)
    {
        var prefix = runId + "|";
        var now = _options.UtcNow();
        foreach (var (key, placed) in _placedPrices)
        {
            if (!key.StartsWith(prefix, StringComparison.Ordinal) || now - placed.PlacedAt < TimeSpan.FromMinutes(1))
            {
                continue;
            }

            var orderPrices = transactions
                .Where(tx => tx.Type == TransactionType.Buy && string.Equals(tx.ItemName?.Trim(), placed.Name, StringComparison.OrdinalIgnoreCase))
                .Select(tx => tx.PriceCopper)
                .ToArray();
            if (orderPrices.Contains(placed.Price))
            {
                _placedPrices.TryRemove(key, out _);
                _logger.LogInformation("Order audit: {Item} buy order confirmed by the API at {Price}.", placed.Name, MarketScanItem.FormatCoins(placed.Price));
                continue;
            }

            if (now - placed.PlacedAt < TimeSpan.FromMinutes(7))
            {
                continue;
            }

            _placedPrices.TryRemove(key, out _);
            if (orderPrices.Length > 0)
            {
                _logger.LogWarning(
                    "Order audit: API shows {Item} buy order(s) at {Prices}, but {Typed} was typed.",
                    placed.Name,
                    string.Join(", ", orderPrices.Select(MarketScanItem.FormatCoins)),
                    MarketScanItem.FormatCoins(placed.Price));
            }
        }
    }

    private static string GetItemKey(string runId, string itemName) =>
        $"{runId}|{itemName.Trim().ToUpperInvariant()}";

    private static string GetCooldownKey(string runId, TradingAction action) =>
        $"{runId}|{action.Kind}|{action.ItemName.Trim().ToUpperInvariant()}";
}

public sealed record TradingReplayRequest(
    IReadOnlyList<ItemCandidate>? Candidates = null,
    IReadOnlyList<string>? Blacklist = null,
    IReadOnlyList<Transaction>? Transactions = null,
    AccountBalance? Balance = null,
    DateTimeOffset? ObservedAt = null,
    ProfitabilityPolicy? Policy = null,
    string Source = "replay",
    string? FixturePath = null,
    string? RecordPath = null,
    string? TemplateRoot = null,
    bool CaptureFrames = false,
    string? CaptureDirectory = null,
    string? VisibleItemName = null,
    string? CandidatesPath = null,
    string? BlacklistPath = null,
    Gw2ClientSettings? ClientSettings = null,
    long? MinimumBalanceCopper = null,
    long? DefaultOrderQuantity = null,
    int OverbidBufferMinCopper = 1,
    int OverbidBufferMaxCopper = 5,
    int UndercutBufferMinCopper = 1,
    int UndercutBufferMaxCopper = 5,
    bool ArmLiveInput = false,
    bool EnableInventorySellPass = false,
    bool EnableOutbidBuyRefresh = true,
    bool UsePortfolioIntelligence = true,
    string? ActiveAccountName = null,
    SessionReportPolicySettings? ReportSettings = null,
    // Re-run the Market Scanner with these filters every AutoRescanIntervalMinutes during the session (0 = off).
    MarketScanFilter? AutoRescanFilter = null,
    int AutoRescanIntervalMinutes = 0)
{
    public IReadOnlyList<ItemCandidate> EffectiveCandidates => Candidates ?? Array.Empty<ItemCandidate>();
    public IReadOnlyList<string> EffectiveBlacklist => Blacklist ?? Array.Empty<string>();
    public IReadOnlyList<Transaction> EffectiveTransactions => Transactions ?? Array.Empty<Transaction>();

    public MarketSnapshot ToMarketSnapshot() => new(
        EffectiveTransactions,
        Balance,
        ObservedAt ?? DateTimeOffset.UtcNow,
        "replay");
}
