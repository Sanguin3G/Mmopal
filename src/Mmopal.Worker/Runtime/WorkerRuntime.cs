using System.Text.Json;
using Microsoft.Extensions.Logging;
using Mmopal.Contracts;
using Mmopal.Gw2;
using Mmopal.Infrastructure;
using Mmopal.Native;

namespace Mmopal.Worker;

public sealed partial class WorkerRuntime
{
    private static readonly JsonSerializerOptions PayloadJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
    private readonly CheckpointStore _store;
    private readonly ILogger _logger;
    private readonly IGw2DataProvider? _marketData;
    private readonly string? _templateRoot;
    private readonly IGw2ClientPreparer _clientPreparer;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly IInputController? _liveInputController;
    private readonly IScreenshotProvider? _liveScreenshots;
    private readonly string? _diagnosticsDirectory;
    private readonly IGw2MarketScanner? _marketScanner;
    private bool _warnedInventoryUnavailable;
    private Gw2ClientSettings _activeClientSettings = new();
    private readonly Dictionary<string, DateTimeOffset> _sellHoldLoggedUtc = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _lastDeliveryUnlockAttemptUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastCharacterSwapUtc = DateTimeOffset.MinValue;
    private static readonly TimeSpan DeliveryUnlockBackoff = TimeSpan.FromMinutes(10);
    // Session-scoped adaptive state (reset at session start).
    private BuyCompetitionTracker _buyCompetition = new();
    private PendingSellInventoryTracker? _sessionSellTracker;
    private readonly Dictionary<string, DateTimeOffset> _relistedAtUtc = new(StringComparer.OrdinalIgnoreCase);
    // First time the Delivery Box was still locked after reopening at the trader on the current character.
    private DateTimeOffset? _deliveryLockedSinceUtc;
    private static readonly TimeSpan DeliveryStuckBeforeSwap = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan MinTimeBetweenDeliverySwaps = TimeSpan.FromMinutes(60);
    // Logging out and in repeatedly gets the account disconnected by the server ("lost its connection ... wait a
    // few minutes before restarting", Code=7:11:3:202:101 after ~12 swaps in 40 minutes).
    private static readonly TimeSpan MinTimeBetweenRecoverySwaps = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan ServerDisconnectCooldown = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan GameRestartBudgetRefill = TimeSpan.FromHours(2);
    private string? _lastLoggedBagFlipItems;
    private readonly ITemplateMatcher? _liveTemplateMatcher;
    private readonly IWindowController? _liveWindowController;
    private readonly TradingLedgerStore? _ledgerStore;
    private readonly SessionReportStore? _sessionReportStore;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _statusLock = new();
    private CancellationTokenSource? _runCancellation;
    private Task? _runTask;
    private string? _runId;
    private bool _pauseRequested;
    // Graceful Stop: finish the current step (an order being typed, a listing), then leave the loop and save the report.
    private bool _stopRequested;
    private static readonly TimeSpan GracefulStopTimeout = TimeSpan.FromSeconds(90);
    private WorkerStatus _status = new(WorkerState.Idle, null, null, null, null, DateTimeOffset.UtcNow);

    public WorkerRuntime(
        CheckpointStore store,
        ILogger logger,
        IGw2DataProvider? marketData = null,
        string? templateRoot = null,
        IGw2ClientPreparer? clientPreparer = null,
        Func<DateTimeOffset>? utcNow = null,
        IInputController? liveInputController = null,
        IScreenshotProvider? liveScreenshots = null,
        ITemplateMatcher? liveTemplateMatcher = null,
        IWindowController? liveWindowController = null,
        TradingLedgerStore? ledgerStore = null,
        SessionReportStore? sessionReportStore = null,
        string? diagnosticsDirectory = null,
        IGw2MarketScanner? marketScanner = null)
    {
        _marketScanner = marketScanner;
        _diagnosticsDirectory = diagnosticsDirectory;
        _store = store;
        _logger = logger;
        _marketData = marketData;
        _templateRoot = templateRoot;
        _clientPreparer = clientPreparer ?? new Gw2ClientPreparer();
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _liveInputController = liveInputController;
        _liveScreenshots = liveScreenshots;
        _liveTemplateMatcher = liveTemplateMatcher;
        _liveWindowController = liveWindowController;
        _ledgerStore = ledgerStore;
        _sessionReportStore = sessionReportStore;
    }

    public event EventHandler<WorkerEvent>? EventRaised;

    public WorkerStatus Status
    {
        get
        {
            lock (_statusLock)
            {
                return _status;
            }
        }
    }

    // A graceful stop also counts as "pause" so the driver and the cycle stop between actions, never mid-action.
    private bool IsPauseRequested
    {
        get
        {
            lock (_statusLock)
            {
                return _pauseRequested || _stopRequested;
            }
        }
    }

    private bool IsStopRequested
    {
        get
        {
            lock (_statusLock)
            {
                return _stopRequested;
            }
        }
    }

    private async Task RunAsync(string runId, BotKind bot, RunMode mode, string profile, string? payload, CancellationToken cancellationToken)
    {
        try
        {
            if (bot == BotKind.TradingPost)
            {
                await RunTradingAsync(runId, mode, profile, payload, cancellationToken);
                return;
            }

            if (mode == RunMode.Live)
            {
                SetStatus(
                    WorkerState.NeedsAttention,
                    bot,
                    mode,
                    profile,
                    "Live input is not armed",
                    "Fishing does not support live input.");
                return;
            }

            SetStatusIfNotPaused(WorkerState.Fishing, bot, mode, profile, "Ready", "Fishing controller is not part of this trading slice.");
            while (!cancellationToken.IsCancellationRequested)
            {
                if (IsPauseRequested)
                {
                    await Task.Delay(100, cancellationToken);
                    continue;
                }

                SetStatusIfNotPaused(WorkerState.Fishing, bot, mode, profile, "Heartbeat", "No live input is issued by the scaffold.");
                var currentState = Status.State;
                await _store.SaveCheckpointAsync(
                    runId,
                    state: currentState.ToString(),
                    payload: new { bot, mode, profile, timestamp = DateTimeOffset.UtcNow },
                    cancellationToken);
                Emit(new WorkerEvent(WorkerEventTypes.Health, JsonSerializer.Serialize(new { state = currentState, timestamp = DateTimeOffset.UtcNow })));
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Worker run cancelled.");
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Worker run failed.");
            SetStatus(WorkerState.Faulted, bot, mode, profile, "Run failed", exception.Message);
            Emit(new WorkerEvent(WorkerEventTypes.Error, exception.ToString()));
        }
    }

    private async Task RunTradingAsync(
        string runId,
        RunMode mode,
        string profileName,
        string? payload,
        CancellationToken cancellationToken)
    {
        var request = string.IsNullOrWhiteSpace(payload)
            ? new TradingReplayRequest()
            : JsonSerializer.Deserialize<TradingReplayRequest>(payload, PayloadJsonOptions)
              ?? throw new InvalidDataException("The trading replay payload is empty.");
        if (mode == RunMode.Live && !request.ArmLiveInput)
        {
            SetStatus(
                WorkerState.NeedsAttention,
                BotKind.TradingPost,
                mode,
                profileName,
                "Live input is not armed",
                "No orders are sent. Enable 'Arm live input' on the Session tab to allow clicks and keys.");
            return;
        }

        var profile = LoadProfile(profileName);
        var effectiveClientSettings = request.ClientSettings ?? new Gw2ClientSettings();
        _activeClientSettings = effectiveClientSettings;
        _lastDeliveryUnlockAttemptUtc = DateTimeOffset.MinValue;
        _lastCharacterSwapUtc = DateTimeOffset.MinValue;
        _buyCompetition = new BuyCompetitionTracker();
        _relistedAtUtc.Clear();
        _deliveryLockedSinceUtc = null;
        var templateRoot = string.IsNullOrWhiteSpace(request.TemplateRoot)
            ? _templateRoot ?? string.Empty
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(request.TemplateRoot));
        var candidates = await ResolveCandidatesAsync(request, cancellationToken);
        var blacklist = await ResolveBlacklistAsync(request, cancellationToken);
        TradingPostMarketReader? observationMarketReader = null;
        FileTradingObservationRecorder? recorder = null;
        IGw2MarketReader reader;
        IScreenshotProvider? sharedScreenshots = _liveScreenshots;
        ITemplateMatcher? sharedTemplates = _liveTemplateMatcher;

        if (string.Equals(request.Source, "screen", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(request.Source, "observe", StringComparison.OrdinalIgnoreCase))
        {
            if (_marketData is null)
            {
                throw new InvalidOperationException("Screen observation requires the official Guild Wars 2 API client.");
            }

            if (request.ClientSettings is { AutoPrepareOnScreenStart: true } clientSettings)
            {
                SetStatusIfNotPaused(
                    WorkerState.LaunchingClient,
                    BotKind.TradingPost,
                    mode,
                    profileName,
                    "Preparing Guild Wars 2 window",
                    $"Ensuring Guild Wars 2 is positioned at (0, 0) sized {clientSettings.WindowWidth}x{clientSettings.WindowHeight}.");
                var prepared = await _clientPreparer.PrepareAsync(
                    clientSettings,
                    clientSettings.AutoLaunchIfNotRunning,
                    cancellationToken);
                _logger.LogInformation("GW2 client preparation: ready={WindowReady}, launched={Launched}, message={Message}",
                    prepared.WindowReady, prepared.Launched, prepared.Message);
                if (!prepared.WindowReady)
                {
                    SetStatus(
                        WorkerState.NeedsAttention,
                        BotKind.TradingPost,
                        mode,
                        profileName,
                        "GW2 window unavailable",
                        prepared.Message);
                    Emit(new WorkerEvent(
                        WorkerEventTypes.Health,
                        JsonSerializer.Serialize(new
                        {
                            state = WorkerState.NeedsAttention.ToString(),
                            succeeded = false,
                            source = request.Source,
                            error = prepared.Message,
                            timestamp = DateTimeOffset.UtcNow
                        },
                        PayloadJsonOptions)));
                    return;
                }
            }

            sharedScreenshots ??= new WindowsDesktopScreenshotProvider();
            sharedTemplates ??= new FrameTemplateMatcher();
            var uiProbe = new FrameTradingPostUiProbe(
                sharedScreenshots,
                sharedTemplates,
                new WindowsOcrReader());
            var observationReader = new ScreenTradingPostObservationReader(
                _marketData,
                uiProbe,
                sharedScreenshots,
                "screen");
            if (!string.IsNullOrWhiteSpace(request.RecordPath))
            {
                recorder = new FileTradingObservationRecorder(request.RecordPath, request.CaptureDirectory);
            }

            observationMarketReader = new TradingPostMarketReader(
                observationReader,
                new TradingPostReadRequest(
                    templateRoot,
                    CaptureFrame: request.CaptureFrames || recorder is not null,
                    ReadVisiblePrice: true,
                    VisibleItemName: request.VisibleItemName),
                recorder);
            reader = observationMarketReader;
        }
        else if (!string.IsNullOrWhiteSpace(request.FixturePath))
        {
            var observationReader = await ReplayTradingPostObservationReader.LoadAsync(request.FixturePath, cancellationToken);
            observationMarketReader = new TradingPostMarketReader(
                observationReader,
                new TradingPostReadRequest(string.Empty, CaptureFrame: false, ReadVisiblePrice: false));
            reader = observationMarketReader;
        }
        else
        {
            reader = new ReplayMarketReader(request.ToMarketSnapshot());
        }

        var effectiveBalance = new EffectiveBalanceState();
        reader = new EffectiveBalanceMarketReader(reader, effectiveBalance, _utcNow);

        Gw2TradingPostUiDriver? liveDriver = null;
        ITradingActionExecutor executor;
        if (mode == RunMode.Live)
        {
            sharedScreenshots ??= new WindowsDesktopScreenshotProvider();
            sharedTemplates ??= new FrameTemplateMatcher();
            liveDriver = new Gw2TradingPostUiDriver(
                sharedScreenshots,
                sharedTemplates,
                _liveInputController ?? new Win32InputController(),
                _liveWindowController ?? new Win32WindowController(),
                templateRoot,
                effectiveClientSettings,
                _logger,
                pollIntervalMs: _liveTemplateMatcher is null ? 150 : 5,
                requireTemplateFilesOnDisk: _liveTemplateMatcher is null,
                isPaused: () => IsPauseRequested,
                diagnosticsDirectory: _diagnosticsDirectory);
            executor = new LiveTradingActionExecutor(
                liveDriver,
                request.OverbidBufferMinCopper,
                request.OverbidBufferMaxCopper);
        }
        else
        {
            executor = mode == RunMode.Observe
                ? (ITradingActionExecutor)new ObserveActionExecutor()
                : new DryRunActionExecutor();
        }

        var apiTimeoutSeconds = Math.Max(15, effectiveClientSettings.ApiTimeoutSeconds);
        var minBalanceCopper = Math.Max(0L, request.MinimumBalanceCopper ?? 0L);
        var controller = new TradingCycleController(
            _store,
            _logger,
            reader,
            executor,
            new TradingControllerOptions
            {
                MaxRetries = Math.Max(0, profile.MaxRetries),
                MaxMarketAge = TimeSpan.FromSeconds(Math.Max(profile.ApiTransactionRefreshSeconds * 2, apiTimeoutSeconds * 2)),
                MaxActionsPerCycle = Math.Max(1, profile.MaxOverbidUpdatesPerPass),
                MinimumBalanceCopper = minBalanceCopper,
                MaxBuyOrderSharePercent = Math.Clamp(effectiveClientSettings.MaxBuyOrderSharePercent, 0, 100)
            });
        var policy = request.Policy ?? new ProfitabilityPolicy();
        var interval = TimeSpan.FromSeconds(Math.Max(2, profile.OverbidScanIntervalSeconds));
        var afkSwapSchedule = new AfkCharacterSwapSchedule(
            effectiveClientSettings.SwapCharacterIntervalMinutes,
            effectiveClientSettings.SwapCharacterJitterMinutes,
            effectiveClientSettings.RandomizeCharacterSelection);
        var sessionCap = new SessionDurationCap(effectiveClientSettings.MaxSessionDurationMinutes, _utcNow());
        var blacklistMatcher = new BlacklistMatcher(blacklist);
        var pendingSellTracker = new PendingSellInventoryTracker();
        _sessionSellTracker = pendingSellTracker;
        var candidateNameSet = candidates
            .Where(c => !string.IsNullOrWhiteSpace(c.ItemName))
            .Select(c => c.ItemName.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var resolvedItemIdCache = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        // A scanned list older than the re-scan interval (for example yesterday's) is refreshed on the first cycle.
        var lastRescanUtc = InitialRescanClock(request, _utcNow());
        var lastRescanCount = candidates.Count;
        var profitRefresher = new LiveCandidateProfitRefresher();
        var lastLedgerSyncUtc = DateTimeOffset.MinValue;
        string? lastLedgerSyncNote = null;

        var maxRecoveryRetries = Math.Clamp(effectiveClientSettings.MaxRecoveryRetries, 0, 10);
        var maxGameRestarts = effectiveClientSettings.AutoRestartGameOnFailure
            ? Math.Clamp(effectiveClientSettings.MaxGameRestartsPerSession, 0, 5)
            : 0;
        var gameRestartsUsed = 0;
        var lastGameRestartUtc = DateTimeOffset.MinValue;
        void SetGameRestartsUsed(int used)
        {
            gameRestartsUsed = used;
            lastGameRestartUtc = _utcNow();
        }

        var consecutiveFailedCycles = 0;

        IReadOnlyList<Gw2LedgerTransaction> cachedLedgerHistory = Array.Empty<Gw2LedgerTransaction>();
        var ledgerLoadedAtUtc = DateTimeOffset.MinValue;
        IReadOnlyList<Transaction> lastKnownActiveOrders = request.EffectiveTransactions;
        long? lastHistoryBuyId = null;
        var lastInventoryApiSyncUtc = DateTimeOffset.MinValue;

        _logger.LogInformation(
            "Auto-list bought flip items from bags: {State}.",
            request.EnableInventorySellPass ? "on" : "off (tick it on the Session page to sell what the bot buys)");
        var reportAccumulator = new SessionReportAccumulator(
            runId,
            request.ActiveAccountName,
            BotKind.TradingPost,
            mode,
            request.Source,
            profileName,
            sessionCap.StartedAt);
        var sessionCapReached = false;
        var sessionOutcome = "Completed";
        var sessionOutcomeSummary = "Trading session completed.";

        // A sleeping PC, display power-off or lock screen stops capture and input to the game.
        using var keepAwake = liveDriver is not null && _liveTemplateMatcher is null ? new KeepAwake() : null;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (gameRestartsUsed > 0 && _utcNow() - lastGameRestartUtc > GameRestartBudgetRefill)
                {
                    // Disconnects on long Asia->EU nights are normal; the restart allowance is per 2 quiet hours,
                    // not per whole session, so a 10-hour run is not halted by its third disconnect.
                    _logger.LogInformation("No game restart for {Hours}h; restart allowance refilled ({Max}).", GameRestartBudgetRefill.TotalHours, maxGameRestarts);
                    gameRestartsUsed = 0;
                }

                if (IsStopRequested)
                {
                    sessionOutcome = "Stopped by User";
                    sessionOutcomeSummary = "Stopped after finishing the current step.";
                    _logger.LogInformation("Stopped after finishing the current step.");
                    break;
                }

                if (IsPauseRequested)
                {
                    await Task.Delay(100, cancellationToken);
                    continue;
                }

                if (sessionCap.IsExpired(_utcNow()))
                {
                    sessionCapReached = true;
                    sessionOutcome = "Time Cap Reached";
                    sessionOutcomeSummary = await FinalizeSessionOnCapAsync(
                        runId,
                        mode,
                        profileName,
                        request,
                        profile,
                        effectiveClientSettings,
                        liveDriver,
                        effectiveBalance,
                        candidateNameSet,
                        cachedLedgerHistory,
                        blacklistMatcher,
                        pendingSellTracker,
                        reportAccumulator,
                        () => lastInventoryApiSyncUtc,
                        ts => lastInventoryApiSyncUtc = ts,
                        () => lastHistoryBuyId,
                        id => lastHistoryBuyId = id).ConfigureAwait(false);
                    return;
                }

                if (liveDriver is not null)
                {
                    var (tpOpen, tpMsg) = await liveDriver.EnsureTradingPostOpenAsync(cancellationToken);
                    if (!tpOpen)
                    {
                        var restartsBefore = gameRestartsUsed;
                        var (recovered, recoverMsg) = await TryRecoverOrRestartLiveClientAsync(
                            liveDriver,
                            effectiveClientSettings,
                            mode,
                            profileName,
                            tpMsg,
                            maxRecoveryRetries,
                            maxGameRestarts,
                            () => gameRestartsUsed,
                            SetGameRestartsUsed,
                            cancellationToken);
                        reportAccumulator.RecordRecoveryAttempt(
                            _utcNow(),
                            "Trading Post Recovery",
                            recovered ? recoverMsg : $"{tpMsg} -> {recoverMsg}",
                            recovered,
                            isGameRestart: gameRestartsUsed > restartsBefore);
                        if (!recovered)
                        {
                            if (IsPauseRequested) continue;
                            sessionOutcome = "Needs Attention";
                            sessionOutcomeSummary = recoverMsg;
                            HaltWithNeedsAttention(mode, profileName, request.Source, "Trading Post unavailable", recoverMsg);
                            return;
                        }
                    }

                    if (IsPauseRequested) continue;

                    var deliveryOutcome = await CollectSmartDeliveryAsync(
                        liveDriver,
                        effectiveBalance,
                        candidateNameSet,
                        blacklistMatcher,
                        pendingSellTracker,
                        reportAccumulator,
                        cancellationToken);
                    if (deliveryOutcome.Collected)
                    {
                        _logger.LogDebug("Delivery pickup complete: {Coins} copper, {Items} flip item(s) queued.", deliveryOutcome.CoinsCollectedCopper, deliveryOutcome.CollectedFlipItemNames.Count);
                    }
                }

                if (IsPauseRequested) continue;

                PortfolioGuidance? portfolioGuidance = null;
                var nowForLedger = _utcNow();
                // Keep the Income & Portfolio ledger current during long sessions: without this, winner boosts and
                // loser guards stay frozen at the last manual "Sync GW2 Account".
                if (request.UsePortfolioIntelligence &&
                    _ledgerStore is not null &&
                    _marketData is Gw2OfficialDataProvider ledgerSource &&
                    nowForLedger - lastLedgerSyncUtc >= LedgerSyncInterval)
                {
                    lastLedgerSyncUtc = nowForLedger;
                    try
                    {
                        var synced = await ledgerSource.SyncPortfolioAsync(_ledgerStore, request.ActiveAccountName, cancellationToken).ConfigureAwait(false);
                        ledgerLoadedAtUtc = DateTimeOffset.MinValue; // re-read below
                        lastLedgerSyncNote = $"synced {nowForLedger:HH:mm} UTC";
                        _logger.LogInformation(
                            "Portfolio sync: {New} new trade(s) archived ({Total} total); history-based boosts and guards refreshed.",
                            synced.NewlyArchivedTransactions,
                            synced.TotalArchivedTransactions);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        lastLedgerSyncNote = "sync failed";
                        _logger.LogWarning("Portfolio sync failed ({Message}); using the last synced history.", ex.Message);
                    }
                }

                if ((request.UsePortfolioIntelligence || request.EnableInventorySellPass) &&
                    _ledgerStore is not null &&
                    (cachedLedgerHistory.Count == 0 || (nowForLedger - ledgerLoadedAtUtc) >= TimeSpan.FromMinutes(5)))
                {
                    try
                    {
                        cachedLedgerHistory = await _ledgerStore.GetTransactionsAsync(request.ActiveAccountName, cancellationToken).ConfigureAwait(false);
                        ledgerLoadedAtUtc = nowForLedger;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogDebug(ex, "Could not load SQLite ledger for portfolio intelligence.");
                    }
                }

                if (request.UsePortfolioIntelligence)
                {
                    portfolioGuidance = PortfolioProfitAdvisor.BuildGuidance(
                        cachedLedgerHistory,
                        lastKnownActiveOrders,
                        nowForLedger,
                        request.DefaultOrderQuantity ?? 1);
                }

                if (request.EnableInventorySellPass)
                {
                    await SyncOwnedFlipInventoryFromApiAsync(
                        liveDriver,
                        effectiveBalance,
                        candidateNameSet,
                        cachedLedgerHistory,
                        blacklistMatcher,
                        pendingSellTracker,
                        reportAccumulator,
                        () => lastInventoryApiSyncUtc,
                        ts => lastInventoryApiSyncUtc = ts,
                        () => lastHistoryBuyId,
                        id => lastHistoryBuyId = id,
                        cancellationToken).ConfigureAwait(false);
                }

                if (request.AutoRescanFilter is { } rescanFilter &&
                    request.AutoRescanIntervalMinutes > 0 &&
                    _marketScanner is not null &&
                    _utcNow() - lastRescanUtc >= TimeSpan.FromMinutes(request.AutoRescanIntervalMinutes))
                {
                    lastRescanUtc = _utcNow();
                    if (await RescanShoppingListAsync(rescanFilter, blacklist, candidates.Count, request.CandidatesPath, cancellationToken).ConfigureAwait(false) is { } fresh)
                    {
                        lastRescanCount = fresh.Count;
                        // The Custom Shopping List (sent inline) stays in the session; only the scanned part is replaced.
                        candidates = MergeWithCustomList(request.EffectiveCandidates, fresh);
                        candidateNameSet = fresh
                            .Where(c => !string.IsNullOrWhiteSpace(c.ItemName))
                            .Select(c => c.ItemName.Trim())
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    }
                }

                var cycleCandidates = await RefreshCycleCandidatesAsync(
                    candidates,
                    resolvedItemIdCache,
                    profitRefresher,
                    policy,
                    request.OverbidBufferMinCopper,
                    request.UndercutBufferMinCopper,
                    portfolioGuidance,
                    cancellationToken);
                var cycleNow = _utcNow();
                if (_buyCompetition.PausedItems(cycleNow).Count > 0)
                {
                    cycleCandidates = cycleCandidates.Where(c => !_buyCompetition.IsPaused(c.ItemName, cycleNow)).ToArray();
                }

                if (IsPauseRequested) continue;

                SetStatusIfNotPaused(WorkerState.ProcessingTrading, BotKind.TradingPost, mode, profileName, "Evaluating market", "Running a deterministic trading cycle.");
                var result = await controller.RunCycleAsync(
                    runId,
                    cycleCandidates,
                    blacklist,
                    policy,
                    cancellationToken,
                    () => IsPauseRequested);

                reportAccumulator.RecordCycle(result.Decisions.Count);
                if (result.Market is not null)
                {
                    reportAccumulator.RecordWalletSnapshot(
                        result.Market.Balance?.Copper,
                        result.Market.Transactions.Count);
                }

                foreach (var actionResult in result.Actions)
                {
                    var actionType = actionResult.Action.Kind switch
                    {
                        TradingActionKind.PlaceBuyOrder => "Buy Order",
                        TradingActionKind.PlaceSellOrder => "Sell Listing",
                        TradingActionKind.CancelBuyOrder => "Cancel Outbid",
                        TradingActionKind.CancelSellOrder => "Cancel Sell",
                        _ => actionResult.Action.Kind.ToString()
                    };
                    var actionStatus = actionResult.Status.ToString();
                    reportAccumulator.RecordTrade(
                        actionResult.CompletedAt,
                        actionType,
                        actionResult.Action.ItemName,
                        actionResult.Action.Quantity,
                        actionResult.Action.PriceCopper,
                        actionStatus,
                        actionResult.Message);
                    if (actionResult.Status == TradingActionStatus.Failed)
                    {
                        reportAccumulator.RecordIncident(
                            actionResult.CompletedAt,
                            "Warning",
                            "Order Action",
                            $"{actionResult.Action.ItemName}: {actionResult.Message}",
                            recovered: false);
                    }
                }

                if (result.Market?.Transactions is { Count: > 0 } marketTransactions)
                {
                    lastKnownActiveOrders = marketTransactions;
                }
                else if (result.Succeeded && result.Market is not null)
                {
                    lastKnownActiveOrders = result.Market.Transactions;
                }

                if (!string.IsNullOrWhiteSpace(result.Error))
                {
                    reportAccumulator.RecordIncident(
                        _utcNow(),
                        result.Succeeded ? "Warning" : "Error",
                        "Trading Cycle",
                        result.Error,
                        recovered: false);
                }

                if (IsPauseRequested)
                {
                    SetStatusIfNotPaused(WorkerState.Paused, BotKind.TradingPost, mode, profileName, "Paused", "Pause requested; no new actions will begin.");
                    continue;
                }

                var soldCount = 0;
                if (liveDriver is not null && request.EnableInventorySellPass)
                {
                    var readyTargets = pendingSellTracker.GetReadyTargets(_utcNow(), Math.Max(1, profile.MaxOverbidUpdatesPerPass));
                    if (readyTargets.Count > 0)
                    {
                        await CollectSmartDeliveryAsync(
                            liveDriver,
                            effectiveBalance,
                            candidateNameSet,
                            blacklistMatcher,
                            pendingSellTracker,
                            reportAccumulator,
                            cancellationToken);
                        if (IsPauseRequested) continue;
                        readyTargets = pendingSellTracker.GetReadyTargets(_utcNow(), Math.Max(1, profile.MaxOverbidUpdatesPerPass));
                        IReadOnlyDictionary<string, SellPricing> sellPricing;
                        (readyTargets, sellPricing) = await KeepProfitableSellTargetsAsync(readyTargets, candidates, request.UndercutBufferMinCopper, cancellationToken).ConfigureAwait(false);
                        if (readyTargets.Count > 0)
                        {
                            var sellResults = await liveDriver.SellInventoryPassDetailedAsync(
                                readyTargets,
                                Math.Max(1, profile.MaxOverbidUpdatesPerPass),
                                request.UndercutBufferMinCopper,
                                request.UndercutBufferMaxCopper,
                                cancellationToken,
                                sellPricing);
                            var postSellNow = _utcNow();
                            pendingSellTracker.RecordSellPassResults(sellResults, postSellNow);
                            foreach (var outcome in sellResults)
                            {
                                _logger.LogInformation("Sell pass {Outcome} for {Item}: {Details}", outcome.Outcome, outcome.ItemName, outcome.Details ?? "-");
                            }
                            foreach (var sellOutcome in sellResults)
                            {
                                if (sellOutcome.Outcome == SellPassItemOutcome.Listed)
                                {
                                    soldCount++;
                                    reportAccumulator.RecordTrade(
                                        postSellNow,
                                        "Sell Listing",
                                        sellOutcome.ItemName,
                                        1,
                                        0,
                                        "Completed",
                                        sellOutcome.Details ?? "Sell order listed from bags.");
                                }
                            }
                        }
                    }
                }

                if (IsPauseRequested) continue;

                var canceledOutbidBuys = 0;
                if (liveDriver is not null && request.EnableOutbidBuyRefresh && result.Market is not null)
                {
                    canceledOutbidBuys = await liveDriver.CancelOutbidBuyOrdersAsync(
                        result.Market.Transactions,
                        Math.Max(1, profile.MaxOverbidUpdatesPerPass),
                        cancellationToken);
                    foreach (var paused in _buyCompetition.RecordOutbidCancels(liveDriver.LastCanceledOutbidItems, _utcNow()))
                    {
                        _logger.LogInformation(
                            "Pausing buys of {Item} for {Hours} h: outbid {Count} times without a single fill (another bot is camping it); its order slots go to items that fill.",
                            paused,
                            (int)BuyCompetitionTracker.PauseDuration.TotalHours,
                            BuyCompetitionTracker.OutbidCancelsBeforePause);
                    }
                    if (canceledOutbidBuys > 0)
                    {
                        reportAccumulator.RecordTrade(
                            _utcNow(),
                            "Outbid Refresh",
                            $"{canceledOutbidBuys} outbid buy order(s)",
                            canceledOutbidBuys,
                            0,
                            "Canceled",
                            "Cleared outbid buy order(s) for fresh bidding.");
                    }
                }

                if (IsPauseRequested) continue;

                if (liveDriver is not null && effectiveClientSettings.RelistStaleUndercutSells && result.Market is not null)
                {
                    await RelistStaleUndercutSellsAsync(liveDriver, result.Market.Transactions, candidateNameSet, request.UndercutBufferMinCopper, reportAccumulator, cancellationToken).ConfigureAwait(false);
                }

                if (IsPauseRequested) continue;

                var now = _utcNow();
                var buyPausedLowGold = result.Market?.Balance is { } bal &&
                                       (bal.Copper <= 0 || (minBalanceCopper > 0 && bal.Copper <= minBalanceCopper));
                var deliveryStuckSwap = IsDeliveryStuckSwapDue(now, effectiveClientSettings.ParkedCharacterCount);
                var characterSwapDue = afkSwapSchedule.IsDue(now) || deliveryStuckSwap;
                if (characterSwapDue)
                {
                    if (liveDriver is not null && effectiveClientSettings.ParkedCharacterCount > 1)
                    {
                        if (deliveryStuckSwap)
                        {
                            _logger.LogInformation(
                                "Swapping character early: the Delivery Box has been locked on {Character} for {Minutes} min (it is not standing next to a Black Lion trader).",
                                liveDriver.CurrentCharacterLabel ?? "this character",
                                (int)(now - _deliveryLockedSinceUtc!.Value).TotalMinutes);
                        }

                        _lastCharacterSwapUtc = now;
                        _deliveryLockedSinceUtc = null;
                        var (swapped, swapMsg) = await liveDriver.RotateParkedCharacterAsync(
                            afkSwapSchedule.RandomizeCharacter,
                            effectiveClientSettings.ParkedCharacterCount,
                            cancellationToken);
                        if (swapped)
                        {
                            reportAccumulator.RecordCharacterSwap(_utcNow(), true, swapMsg);
                            pendingSellTracker.CharacterChanged(_utcNow());
                        }
                        else
                        {
                            var restartsBeforeSwap = gameRestartsUsed;
                            var (recoveredSwap, recoverSwapMsg) = await TryRecoverOrRestartLiveClientAsync(
                                liveDriver,
                                effectiveClientSettings,
                                mode,
                                profileName,
                                swapMsg,
                                maxRecoveryRetries,
                                maxGameRestarts,
                                () => gameRestartsUsed,
                                SetGameRestartsUsed,
                                cancellationToken);
                            reportAccumulator.RecordRecoveryAttempt(
                                _utcNow(),
                                "Character Swap Recovery",
                                recoverSwapMsg,
                                recoveredSwap,
                                isGameRestart: gameRestartsUsed > restartsBeforeSwap);
                            reportAccumulator.RecordCharacterSwap(_utcNow(), recoveredSwap, recoveredSwap ? recoverSwapMsg : swapMsg);
                            if (!recoveredSwap)
                            {
                                if (IsPauseRequested) continue;
                                sessionOutcome = "Needs Attention";
                                sessionOutcomeSummary = recoverSwapMsg;
                                HaltWithNeedsAttention(mode, profileName, request.Source, "Character swap failed", recoverSwapMsg);
                                return;
                            }
                        }
                    }
                    else
                    {
                        _logger.LogInformation("AFK character swap is due ({Summary}); advancing next schedule.", afkSwapSchedule.Describe(now));
                        reportAccumulator.RecordCharacterSwap(now, true, "Simulated/single-character AFK rotation interval ticked.");
                    }

                    afkSwapSchedule.MarkSwapped(now);
                }

                var characterSwapSummary = afkSwapSchedule.Describe(now);
                var executedActions = result.Actions.Count(a => a.Status != TradingActionStatus.Skipped);
                var skippedActions = result.Actions.Count - executedActions;
                var baseSummary = result.Error ?? (candidates.Count == 0
                    ? "No candidates supplied; 0 decisions evaluated."
                    : skippedActions > 0
                        ? $"Decisions: {result.Decisions.Count}; actions: {executedActions} ({skippedActions} skipped)."
                        : $"Decisions: {result.Decisions.Count}; actions: {executedActions}.");
                var extras = new List<string>();
                if (soldCount > 0) extras.Add($"listed {soldCount} sell");
                if (canceledOutbidBuys > 0) extras.Add($"refreshed {canceledOutbidBuys} outbid buy");
                if (buyPausedLowGold) extras.Add("Buys paused (0g/reserve floor)");
                var summaryMessage = extras.Count > 0
                    ? $"{baseSummary} ({string.Join(", ", extras)})"
                    : baseSummary;

                SetStatusIfNotPaused(
                    result.Succeeded ? WorkerState.TradingPostReady : WorkerState.Recovering,
                    BotKind.TradingPost,
                    mode,
                    profileName,
                    result.Succeeded ? "Cycle complete" : "Cycle requires recovery",
                    summaryMessage);
                Emit(new WorkerEvent(
                    WorkerEventTypes.Health,
                    JsonSerializer.Serialize(new
                    {
                        state = result.FinalState.ToString(),
                        succeeded = result.Succeeded,
                        decisions = result.Decisions.Count,
                        actions = executedActions,
                        skippedActions,
                        soldCount,
                        canceledOutbidBuys,
                        pendingSellCount = pendingSellTracker.Count,
                        buyPausedLowGold,
                        source = request.Source,
                        observation = observationMarketReader?.LastRead?.Observation.Status.ToString(),
                        ui = observationMarketReader?.LastRead?.Observation.Ui,
                        visiblePrice = observationMarketReader?.LastRead?.Observation.VisiblePrice,
                        timings = observationMarketReader?.LastRead?.Observation.TimingsMilliseconds,
                        orderBook = OrderBookReport.Summarize(result.Market?.Transactions).Describe(),
                        characterSwap = characterSwapSummary,
                        characterSwapDue,
                        nextCharacterSwapAt = afkSwapSchedule.NextSwapAt,
                        sessionCap = sessionCap.Describe(now),
                        marketList = DescribeMarketList(request, candidates.Count, lastRescanCount, lastRescanUtc, now) +
                                     (_buyCompetition.PausedItems(now).Count is var botWar and > 0 ? $" · {botWar} paused (outbid, no fills)" : string.Empty),
                        portfolio = DescribePortfolio(request, portfolioGuidance, lastLedgerSyncNote),
                        sessionStartedAt = sessionCap.StartedAt,
                        sessionExpiresAt = sessionCap.ExpiresAt,
                        parkedCharacters = effectiveClientSettings.ParkedCharacterCount,
                        error = result.Error,
                        timestamp = now
                    },
                    PayloadJsonOptions)));

                if (result.Succeeded)
                {
                    consecutiveFailedCycles = 0;
                }
                else if (liveDriver is not null)
                {
                    consecutiveFailedCycles++;
                    var maxUnrecoveredBudget = Math.Max(3, maxRecoveryRetries + maxGameRestarts + 2);
                    if (consecutiveFailedCycles >= maxUnrecoveredBudget)
                    {
                        sessionOutcome = "Needs Attention";
                        sessionOutcomeSummary = result.Error ?? $"Cycle failed {consecutiveFailedCycles} times consecutively.";
                        HaltWithNeedsAttention(
                            mode,
                            profileName,
                            request.Source,
                            "Recovery limit reached",
                            sessionOutcomeSummary);
                        return;
                    }

                    if (maxRecoveryRetries > 0 || gameRestartsUsed < maxGameRestarts)
                    {
                        var restartsBeforeCycle = gameRestartsUsed;
                        var (recoveredCycle, recoverCycleMsg) = await TryRecoverOrRestartLiveClientAsync(
                            liveDriver,
                            effectiveClientSettings,
                            mode,
                            profileName,
                            result.Error ?? "Trading cycle failed",
                            maxRecoveryRetries,
                            maxGameRestarts,
                            () => gameRestartsUsed,
                            SetGameRestartsUsed,
                            cancellationToken);
                        reportAccumulator.RecordRecoveryAttempt(
                            _utcNow(),
                            "Cycle Recovery",
                            recoverCycleMsg,
                            recoveredCycle,
                            isGameRestart: gameRestartsUsed > restartsBeforeCycle);
                        if (!recoveredCycle)
                        {
                            if (IsPauseRequested) continue;
                            sessionOutcome = "Needs Attention";
                            sessionOutcomeSummary = recoverCycleMsg;
                            HaltWithNeedsAttention(mode, profileName, request.Source, "Recovery failed", recoverCycleMsg);
                            return;
                        }
                    }
                }

                if (sessionCap.IsExpired(_utcNow()))
                {
                    continue;
                }

                var waitDuration = interval;
                if (sessionCap.ExpiresAt is { } expiresAt)
                {
                    var remainingBeforeCap = expiresAt - _utcNow();
                    if (remainingBeforeCap <= TimeSpan.Zero)
                    {
                        continue;
                    }

                    if (remainingBeforeCap < waitDuration)
                    {
                        waitDuration = remainingBeforeCap;
                    }
                }

                await WaitIntervalOrPauseAsync(waitDuration, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            sessionOutcome = "Stopped by User";
            sessionOutcomeSummary = "Trading session stopped by operator.";
            throw;
        }
        catch (Exception ex)
        {
            sessionOutcome = "Faulted";
            sessionOutcomeSummary = ex.Message;
            reportAccumulator.RecordIncident(_utcNow(), "Error", "Worker Exception", ex.Message, recovered: false);
            throw;
        }
        finally
        {
            if (recorder is not null)
            {
                await recorder.DisposeAsync();
            }

            try
            {
                var finalReport = reportAccumulator.Build(_utcNow(), sessionOutcome, sessionOutcomeSummary);
                var policySettings = request.ReportSettings ?? new SessionReportPolicySettings();
                var shouldSaveReport = SessionReportPolicy.ShouldSave(finalReport, policySettings);
                if (shouldSaveReport && _sessionReportStore is not null)
                {
                    try
                    {
                        await _sessionReportStore.SaveReportAsync(
                            finalReport,
                            policySettings.MaxSavedReports,
                            CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception saveEx)
                    {
                        _logger.LogWarning(saveEx, "Failed to save session report {ReportId} to SQLite.", finalReport.Id);
                        shouldSaveReport = false;
                    }
                }

                var notification = new SessionReportNotification(
                    Report: finalReport,
                    Saved: shouldSaveReport,
                    CapReached: sessionCapReached,
                    AutoCloseApp: sessionCapReached && effectiveClientSettings.AutoCloseAppOnSessionCap,
                    ShutdownPc: sessionCapReached && effectiveClientSettings.ShutdownPcOnSessionCap,
                    ShutdownCountdownSeconds: Math.Clamp(effectiveClientSettings.PcShutdownCountdownSeconds, 10, 600));
                Emit(new WorkerEvent(
                    WorkerEventTypes.SessionReport,
                    JsonSerializer.Serialize(notification, PayloadJsonOptions),
                    Timestamp: _utcNow()));
            }
            catch (Exception emitEx)
            {
                _logger.LogWarning(emitEx, "Failed to build/emit session report event.");
            }
        }
    }

    private async Task WaitIntervalOrPauseAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + interval;
        while (!cancellationToken.IsCancellationRequested)
        {
            if (IsPauseRequested)
            {
                return;
            }

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return;
            }

            var slice = remaining > TimeSpan.FromMilliseconds(200) ? TimeSpan.FromMilliseconds(200) : remaining;
            await Task.Delay(slice, cancellationToken);
        }
    }

    private static RunProfile LoadProfile(string profileName)
    {
        try
        {
            return new ProfileLoader().Load(profileName, AppContext.BaseDirectory);
        }
        catch (FileNotFoundException)
        {
            return new RunProfile { Name = profileName };
        }
    }

    private static readonly TimeSpan LedgerSyncInterval = TimeSpan.FromMinutes(45);

    /// <summary>
    /// Tracks recently collected Delivery Box coins so GW2's ~5 minute cached wallet/delivery
    /// endpoints do not falsely pause buy orders below the gold reserve floor right after Take All.
    /// </summary>
    internal sealed class EffectiveBalanceState
    {
        private static readonly TimeSpan StaleCollectionCooldown = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan ApiCacheWindow = TimeSpan.FromMinutes(5);

        public long? LastObservedApiWalletCopper { get; private set; }
        public long PendingCreditCoinsCopper { get; private set; }
        public long ApiWalletAtCollectionCopper { get; private set; }
        public DateTimeOffset? CollectedAtUtc { get; private set; }
        private long _lastSeenDeliveryCoins;
        private int _lastSeenDeliveryItems;
        private DateTimeOffset? _lastTakeAllAttemptUtc;
        private bool _lastTakeAllSucceeded;

        public void ClearDeliveryCleared()
        {
            PendingCreditCoinsCopper = 0;
            CollectedAtUtc = null;
            _lastSeenDeliveryCoins = 0;
            _lastSeenDeliveryItems = 0;
            _lastTakeAllAttemptUtc = null;
            _lastTakeAllSucceeded = false;
        }

        public bool ShouldSkipStaleDeliveryCollection(long deliveryCoinsCopper, int itemCount, DateTimeOffset nowUtc)
        {
            if (deliveryCoinsCopper <= 0 && itemCount <= 0)
            {
                return false;
            }

            // After a successful Take All the delivery API keeps reporting the same coins and items until its ~5 min
            // cache expires; re-collecting then clicks an empty box and double-counts the pickup in the session
            // report (seen live: "24g 83s 06c, 1 item" collected four times). A failed click is retried sooner.
            var window = _lastTakeAllSucceeded ? ApiCacheWindow : StaleCollectionCooldown;
            return deliveryCoinsCopper == _lastSeenDeliveryCoins &&
                   itemCount == _lastSeenDeliveryItems &&
                   _lastTakeAllAttemptUtc.HasValue &&
                   (nowUtc - _lastTakeAllAttemptUtc.Value) < window;
        }

        public void RecordTakeAllAttempt(long deliveryCoinsCopper, long apiWalletBaselineCopper, bool clickedTakeAll, DateTimeOffset nowUtc, int itemCount = 0)
        {
            _lastSeenDeliveryCoins = deliveryCoinsCopper;
            _lastSeenDeliveryItems = itemCount;
            _lastTakeAllAttemptUtc = nowUtc;
            _lastTakeAllSucceeded = clickedTakeAll;
            if (clickedTakeAll && deliveryCoinsCopper > 0)
            {
                PendingCreditCoinsCopper = deliveryCoinsCopper;
                ApiWalletAtCollectionCopper = apiWalletBaselineCopper;
                CollectedAtUtc = nowUtc;
            }
        }

        public MarketSnapshot ApplyEffectiveBalance(MarketSnapshot snapshot, DateTimeOffset nowUtc)
        {
            if (snapshot.Balance is not { } bal)
            {
                return snapshot;
            }

            LastObservedApiWalletCopper = bal.Copper;
            if (PendingCreditCoinsCopper <= 0 || CollectedAtUtc is null)
            {
                return snapshot;
            }

            if ((nowUtc - CollectedAtUtc.Value) >= ApiCacheWindow ||
                bal.Copper >= ApiWalletAtCollectionCopper + PendingCreditCoinsCopper)
            {
                PendingCreditCoinsCopper = 0;
                CollectedAtUtc = null;
                return snapshot;
            }

            return snapshot with
            {
                Balance = new AccountBalance(bal.Copper + PendingCreditCoinsCopper)
            };
        }
    }

    private sealed class EffectiveBalanceMarketReader(
        IGw2MarketReader inner,
        EffectiveBalanceState balanceState,
        Func<DateTimeOffset> utcNow) : IGw2MarketReader
    {
        public async Task<MarketSnapshot> ReadAsync(CancellationToken cancellationToken = default)
        {
            var snapshot = await inner.ReadAsync(cancellationToken).ConfigureAwait(false);
            return balanceState.ApplyEffectiveBalance(snapshot, utcNow());
        }
    }
}
