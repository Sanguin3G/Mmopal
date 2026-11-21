using System.Text.Json;
using Microsoft.Extensions.Logging;
using Mmopal.Contracts;
using Mmopal.Gw2;
using Mmopal.Infrastructure;
using Mmopal.Native;

namespace Mmopal.Worker;

public sealed class WorkerRuntime
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

    private bool IsPauseRequested
    {
        get
        {
            lock (_statusLock)
            {
                return _pauseRequested;
            }
        }
    }

    public async Task<WorkerEvent> HandleAsync(WorkerCommand command, CancellationToken cancellationToken = default)
    {
        if (command is null || string.IsNullOrWhiteSpace(command.Type))
        {
            return ErrorEvent("Worker command type is required.");
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            return command.Type.ToLowerInvariant() switch
            {
                WorkerCommandTypes.Start => await StartAsync(command, cancellationToken),
                WorkerCommandTypes.Pause => Pause(),
                WorkerCommandTypes.Resume => Resume(),
                WorkerCommandTypes.Stop => await StopCommandAsync(cancellationToken),
                WorkerCommandTypes.EmergencyStop => await EmergencyStopAsync(cancellationToken),
                WorkerCommandTypes.Status => StatusEvent(),
                WorkerCommandTypes.Shutdown => await StopCommandAsync(cancellationToken),
                _ => ErrorEvent($"Unknown worker command '{command.Type}'.")
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_statusLock)
        {
            _pauseRequested = false;
        }

        _runCancellation?.Cancel();
        if (_runTask is not null)
        {
            try { await _runTask.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { }
        }

        _runCancellation?.Dispose();
        _runCancellation = null;
        _runTask = null;
        if (_runId is not null)
        {
            await _store.CompleteRunAsync(_runId, WorkerState.Stopped.ToString(), cancellationToken);
        }

        _runId = null;
        SetStatus(WorkerState.Stopped, message: "Worker stopped.");
    }

    private async Task<WorkerEvent> StartAsync(WorkerCommand command, CancellationToken cancellationToken)
    {
        if (_runTask is not null && !_runTask.IsCompleted)
        {
            return ErrorEvent("A run is already active.");
        }

        var bot = command.Bot ?? BotKind.TradingPost;
        var mode = command.Mode ?? RunMode.DryRun;
        var profile = command.Profile ?? "balanced";
        _runId = Guid.NewGuid().ToString("N");
        await _store.StartRunAsync(_runId, bot.ToString(), mode.ToString(), profile, cancellationToken);
        _runCancellation = new CancellationTokenSource();
        lock (_statusLock)
        {
            _pauseRequested = false;
        }

        SetStatus(WorkerState.Starting, bot, mode, profile, "Starting", "Run requested.");

        _runTask = RunAsync(_runId, bot, mode, profile, command.Payload, _runCancellation.Token);
        await Task.Yield();
        return StatusEvent(command.RequestId);
    }

    private WorkerEvent Pause()
    {
        WorkerEvent? statusEvent = null;
        lock (_statusLock)
        {
            if (_status.State is WorkerState.Starting
                or WorkerState.LaunchingClient
                or WorkerState.WaitingForLogin
                or WorkerState.TradingPostReady
                or WorkerState.ProcessingTrading
                or WorkerState.Recovering
                or WorkerState.Fishing)
            {
                _pauseRequested = true;
                _status = _status with
                {
                    State = WorkerState.Paused,
                    Timestamp = DateTimeOffset.UtcNow,
                    Message = "Pause requested; no new actions will begin."
                };
                statusEvent = CreateStatusEvent(_status);
            }
        }

        if (statusEvent is not null)
        {
            Emit(statusEvent);
        }

        return StatusEvent();
    }

    private WorkerEvent Resume()
    {
        WorkerEvent? statusEvent = null;
        lock (_statusLock)
        {
            if (_status.State == WorkerState.Paused || _pauseRequested)
            {
                _pauseRequested = false;
                var nextState = _status.Bot == BotKind.Fishing ? WorkerState.Fishing : WorkerState.ProcessingTrading;
                _status = _status with
                {
                    State = nextState,
                    Timestamp = DateTimeOffset.UtcNow,
                    Message = "Run resumed."
                };
                statusEvent = CreateStatusEvent(_status);
            }
        }

        if (statusEvent is not null)
        {
            Emit(statusEvent);
        }

        return StatusEvent();
    }

    private async Task<WorkerEvent> StopCommandAsync(CancellationToken cancellationToken)
    {
        await StopAsync(cancellationToken);
        return StatusEvent();
    }

    private async Task<WorkerEvent> EmergencyStopAsync(CancellationToken cancellationToken)
    {
        _runCancellation?.Cancel();
        await StopAsync(cancellationToken);
        SetStatus(WorkerState.Stopped, message: "Emergency stop completed.");
        return StatusEvent();
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

    private static async Task<IReadOnlyList<ItemCandidate>> ResolveCandidatesAsync(
        TradingReplayRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.CandidatesPath))
        {
            return request.EffectiveCandidates;
        }

        var loaded = await CandidatesLoader.LoadCandidatesAsync(request.CandidatesPath, cancellationToken);
        if (request.EffectiveCandidates.Count == 0)
        {
            return loaded;
        }

        return request.EffectiveCandidates.Concat(loaded).ToArray();
    }

    private static async Task<IReadOnlyList<string>> ResolveBlacklistAsync(
        TradingReplayRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.BlacklistPath))
        {
            return request.EffectiveBlacklist;
        }

        var loaded = await CandidatesLoader.LoadBlacklistAsync(request.BlacklistPath, cancellationToken);
        if (request.EffectiveBlacklist.Count == 0)
        {
            return loaded;
        }

        return request.EffectiveBlacklist.Concat(loaded).ToArray();
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

    private WorkerEvent StatusEvent(string? correlationId = null) => CreateStatusEvent(Status, correlationId);

    private static WorkerEvent CreateStatusEvent(WorkerStatus status, string? correlationId = null) =>
        new(WorkerEventTypes.Status, JsonSerializer.Serialize(status), correlationId, DateTimeOffset.UtcNow);

    private WorkerEvent ErrorEvent(string message)
    {
        _logger.LogWarning("{Message}", message);
        var result = new WorkerEvent(WorkerEventTypes.Error, message, Timestamp: DateTimeOffset.UtcNow);
        Emit(result);
        return result;
    }

    private void SetStatus(
        WorkerState state,
        BotKind? bot = null,
        RunMode? mode = null,
        string? profile = null,
        string? lastAction = null,
        string? message = null)
    {
        WorkerEvent statusEvent;
        lock (_statusLock)
        {
            _status = _status with
            {
                State = state,
                Bot = bot ?? _status.Bot,
                Mode = mode ?? _status.Mode,
                Profile = profile ?? _status.Profile,
                LastAction = lastAction ?? _status.LastAction,
                Timestamp = DateTimeOffset.UtcNow,
                Message = message
            };
            statusEvent = CreateStatusEvent(_status);
        }

        Emit(statusEvent);
    }

    private void SetStatusIfNotPaused(
        WorkerState state,
        BotKind? bot = null,
        RunMode? mode = null,
        string? profile = null,
        string? lastAction = null,
        string? message = null)
    {
        WorkerEvent statusEvent;
        lock (_statusLock)
        {
            _status = _status with
            {
                State = _pauseRequested ? WorkerState.Paused : state,
                Bot = bot ?? _status.Bot,
                Mode = mode ?? _status.Mode,
                Profile = profile ?? _status.Profile,
                LastAction = lastAction ?? _status.LastAction,
                Timestamp = DateTimeOffset.UtcNow,
                Message = _pauseRequested ? "Paused; cycle completed and no new actions will begin." : message
            };
            statusEvent = CreateStatusEvent(_status);
        }

        Emit(statusEvent);
    }

    private async Task<(bool Collected, long CoinsCollectedCopper, IReadOnlyList<string> CollectedFlipItemNames)> CollectSmartDeliveryAsync(
        Gw2TradingPostUiDriver liveDriver,
        EffectiveBalanceState effectiveBalance,
        IReadOnlySet<string> candidateNameSet,
        BlacklistMatcher blacklistMatcher,
        PendingSellInventoryTracker pendingSellTracker,
        SessionReportAccumulator? reportAccumulator,
        CancellationToken cancellationToken)
    {
        if (_marketData is Gw2OfficialDataProvider officialData)
        {
            try
            {
                var now = _utcNow();
                var delivery = await officialData.GetDeliveryAsync(cancellationToken).ConfigureAwait(false);
                if (delivery.CoinsCopper <= 0 && delivery.Items.Count == 0)
                {
                    effectiveBalance.ClearDeliveryCleared();
                    return (false, 0, Array.Empty<string>());
                }

                if (effectiveBalance.ShouldSkipStaleDeliveryCollection(delivery.CoinsCopper, delivery.Items.Count, now))
                {
                    return (false, 0, Array.Empty<string>());
                }

                var totalItemCount = delivery.Items.Sum(entry => entry.Count);
                _logger.LogInformation(
                    "GW2 Delivery Box ready: {Coins} coins, {Count} item(s) across {Stacks} slot(s); collecting.",
                    MarketScanItem.FormatCoins(delivery.CoinsCopper),
                    totalItemCount,
                    delivery.Items.Count);

                var collectedFlipItems = new List<string>();
                if (delivery.Items.Count > 0)
                {
                    var itemMeta = await officialData.ResolveItemMetadataAsync(
                        delivery.Items.Select(entry => entry.ItemId),
                        _ledgerStore,
                        cancellationToken).ConfigureAwait(false);
                    foreach (var entry in delivery.Items)
                    {
                        if (itemMeta.TryGetValue(entry.ItemId, out var info) &&
                            !string.IsNullOrWhiteSpace(info.Name) &&
                            candidateNameSet.Contains(info.Name.Trim()) &&
                            !blacklistMatcher.IsBlocked(info.Name))
                        {
                            collectedFlipItems.Add(info.Name.Trim());
                        }
                    }
                }

                var apiWalletBaseline = effectiveBalance.LastObservedApiWalletCopper
                    ?? (await officialData.GetBalanceAsync(cancellationToken).ConfigureAwait(false))?.Copper
                    ?? 0;
                var collectedUi = await liveDriver.CollectDeliveryBoxAsync(cancellationToken).ConfigureAwait(false);
                if (!collectedUi && liveDriver.DeliveryNeedsTraderNpc)
                {
                    collectedUi = await UnlockDeliveryAtTraderAsync(liveDriver, cancellationToken).ConfigureAwait(false);
                }

                effectiveBalance.RecordTakeAllAttempt(delivery.CoinsCopper, apiWalletBaseline, collectedUi, now, delivery.Items.Count);
                if (collectedUi)
                {
                    if (collectedFlipItems.Count > 0)
                    {
                        pendingSellTracker.RecordDeliveryPickup(collectedFlipItems, now);
                    }

                    reportAccumulator?.RecordDeliveryPickup(
                        now,
                        delivery.CoinsCopper,
                        delivery.Items.Count,
                        collectedFlipItems.Count > 0
                            ? $"Picked up {MarketScanItem.FormatCoins(delivery.CoinsCopper)} and {string.Join(", ", collectedFlipItems.Take(3))}."
                            : $"Picked up {MarketScanItem.FormatCoins(delivery.CoinsCopper)}.");

                    if (delivery.CoinsCopper > 0)
                    {
                        _logger.LogInformation(
                            "Collected {Coins} via Take All; crediting live balance temporarily if GW2 API wallet cache lags.",
                            MarketScanItem.FormatCoins(delivery.CoinsCopper));
                    }
                }

                return (collectedUi, collectedUi ? delivery.CoinsCopper : 0, collectedFlipItems);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogDebug(exception, "Delivery API check failed; falling back to visual Delivery Box check.");
            }
        }

        var visualCollected = await liveDriver.CollectDeliveryBoxAsync(cancellationToken).ConfigureAwait(false);
        return (visualCollected, 0, Array.Empty<string>());
    }

    /// <summary>
    /// The Delivery Box only pays out when the TP was opened at a Black Lion trader. Reopen it through the NPC at
    /// most once every 10 minutes; otherwise leave the coins for later (buying and selling still work). This never
    /// switches characters: doing that on every locked cycle caused ~12 logouts in 40 minutes and a server kick.
    /// </summary>
    private async Task<bool> UnlockDeliveryAtTraderAsync(Gw2TradingPostUiDriver liveDriver, CancellationToken cancellationToken)
    {
        var now = _utcNow();
        if (now - _lastDeliveryUnlockAttemptUtc < DeliveryUnlockBackoff)
        {
            return false;
        }

        _lastDeliveryUnlockAttemptUtc = now;
        var (reopened, message) = await liveDriver.ReopenTradingPostAtTraderAsync(cancellationToken).ConfigureAwait(false);
        if (!reopened)
        {
            _logger.LogWarning("Could not reopen the Trading Post at the trader: {Message}", message);
            return false;
        }

        var collected = await liveDriver.CollectDeliveryBoxAsync(cancellationToken).ConfigureAwait(false);
        if (collected)
        {
            _deliveryLockedSinceUtc = null;
            _logger.LogInformation("Delivery Box unlocked at the trader and collected.");
        }
        else
        {
            _deliveryLockedSinceUtc ??= now;
            _logger.LogInformation(
                "Delivery Box still locked after reopening at the trader on {Character}; trying again in 10 minutes. Move that character right next to a Black Lion trader (after {Minutes} min locked Mmopal swaps to another character).",
                liveDriver.CurrentCharacterLabel ?? "this character",
                (int)DeliveryStuckBeforeSwap.TotalMinutes);
        }

        return collected;
    }

    /// <summary>
    /// Drops sell targets whose listing would not cover what was paid. The sell pass lists at the lowest ask minus
    /// the undercut; after the 5% listing and 10% exchange fees that must still cover the most Mmopal paid for the
    /// item (its planned buy price; else the shopping list's buy price). Held items stay queued and are rechecked
    /// on later passes, so they list once the market recovers. Unknown cost or quote: listed as before.
    /// </summary>
    private async Task<(IReadOnlyList<string> Targets, IReadOnlyDictionary<string, SellPricing> Pricing)> KeepProfitableSellTargetsAsync(
        IReadOnlyList<string> targets,
        IReadOnlyList<ItemCandidate> candidates,
        int undercutMinCopper,
        CancellationToken cancellationToken)
    {
        var pricing = new Dictionary<string, SellPricing>(StringComparer.OrdinalIgnoreCase);
        if (targets.Count == 0 || _marketData is not Gw2OfficialDataProvider officialData || _ledgerStore is null)
        {
            return (targets, pricing);
        }

        try
        {
            var costs = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in candidates)
            {
                if (candidate.BuyPriceCopper is { } buy && buy > 0)
                {
                    costs[candidate.ItemName.Trim()] = buy;
                }
            }

            foreach (var (name, paid) in await _store.GetOrderedItemCostsAsync(_utcNow().AddDays(-30), cancellationToken).ConfigureAwait(false))
            {
                costs[name.Trim()] = Math.Max(paid, costs.GetValueOrDefault(name.Trim()));
            }

            var ids = await _ledgerStore.GetItemIdsByNamesAsync(targets.ToArray(), cancellationToken).ConfigureAwait(false);
            var quotes = ids.Count == 0
                ? new Dictionary<int, Gw2LivePriceQuote>()
                : await officialData.GetLiveMarketQuotesAsync(ids.Values.Distinct().ToArray(), cancellationToken).ConfigureAwait(false);
            var profitability = new ProfitabilityEngine();
            var keep = new List<string>(targets.Count);
            foreach (var name in targets)
            {
                // Typed sell pricing: API lowest ask (resolves the row text, fallback price) and the break-even floor.
                long? apiLowestAsk = ids.TryGetValue(name.Trim(), out var pricedId) && quotes.TryGetValue(pricedId, out var pricedQuote) && pricedQuote.LowestSellCopper > 0
                    ? pricedQuote.LowestSellCopper
                    : null;
                long? breakEven = costs.TryGetValue(name.Trim(), out var knownCost) ? SellPricing.BreakEvenListingPrice(knownCost) : null;
                pricing[name.Trim()] = new SellPricing(apiLowestAsk, breakEven);

                if (!costs.TryGetValue(name.Trim(), out var cost) ||
                    !ids.TryGetValue(name.Trim(), out var id) ||
                    !quotes.TryGetValue(id, out var quote) ||
                    quote.LowestSellCopper <= 0)
                {
                    keep.Add(name);
                    continue;
                }

                var listPrice = Math.Max(1, quote.LowestSellCopper - Math.Max(0, undercutMinCopper));
                var outcome = profitability.Evaluate(cost, listPrice, new ProfitabilityPolicy());
                if (outcome.IsValid && outcome.ProfitCopper >= 0)
                {
                    keep.Add(name);
                    continue;
                }

                if (!_sellHoldLoggedUtc.TryGetValue(name, out var logged) || _utcNow() - logged > TimeSpan.FromMinutes(30))
                {
                    _sellHoldLoggedUtc[name] = _utcNow();
                    _logger.LogInformation(
                        "Holding {Item}: listing at {List} would lose {Loss} after fees against the {Cost} paid; will retry when the price recovers.",
                        name,
                        MarketScanItem.FormatCoins(listPrice),
                        MarketScanItem.FormatCoins(Math.Abs(outcome.ProfitCopper)),
                        MarketScanItem.FormatCoins(cost));
                }
            }

            return (keep, pricing);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Sell profit check skipped for this pass.");
            return (targets, pricing);
        }
    }

    /// <summary>
    /// Re-runs the Market Scanner with the session's filters so a long session follows the market instead of a list
    /// frozen at start. Keeps the old list when the scan fails or finds fewer than 5 items, and saves the new list to
    /// the shopping-list file so the app shows it. Open orders and bag items are unaffected: Outbid Refresh works from
    /// the account's orders and the sell pass from everything Mmopal ordered.
    /// </summary>
    private async Task<IReadOnlyList<ItemCandidate>?> RescanShoppingListAsync(
        MarketScanFilter filter,
        IReadOnlyList<string> blacklist,
        int previousCount,
        string? candidatesPath,
        CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Auto re-scan: scanning the market with the session's filters...");
            var results = await _marketScanner!.ScanAsync(filter, blacklist, progress: null, cancellationToken).ConfigureAwait(false);
            if (results.Count < 5)
            {
                _logger.LogWarning("Auto re-scan found only {Count} item(s); keeping the current {Previous}-item list.", results.Count, previousCount);
                return null;
            }

            var fresh = results.Select(item => item.ToCandidate()).ToArray();
            if (!string.IsNullOrWhiteSpace(candidatesPath) && candidatesPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                OperatorListsStore.SaveShoppingList(candidatesPath, fresh);
            }

            _logger.LogInformation(
                "Auto re-scan: shopping list updated to {Count} items (was {Previous}). Top: {Top}",
                fresh.Length,
                previousCount,
                string.Join(", ", results.Take(3).Select(item => item.ItemName)));
            return fresh;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("Auto re-scan failed ({Message}); keeping the current list.", ex.Message);
            return null;
        }
    }

    private static readonly TimeSpan LedgerSyncInterval = TimeSpan.FromMinutes(45);

    /// <summary>
    /// Early swap when the Delivery Box has been locked for 30+ min (this character is not at a trader, so coins and
    /// items pile up until the next AFK swap, up to 2.5 h). At most once an hour: swapping on every locked cycle once
    /// caused ~12 logouts in 40 minutes and a server kick.
    /// </summary>
    private bool IsDeliveryStuckSwapDue(DateTimeOffset nowUtc, int parkedCharacterCount) =>
        parkedCharacterCount > 1 &&
        _deliveryLockedSinceUtc is { } since &&
        nowUtc - since >= DeliveryStuckBeforeSwap &&
        nowUtc - _lastCharacterSwapUtc >= MinTimeBetweenDeliverySwaps;

    /// <summary>Cancels listings that have stayed undercut 12 h+ when relisting still covers cost and the lost fee.</summary>
    private async Task RelistStaleUndercutSellsAsync(
        Gw2TradingPostUiDriver liveDriver,
        IReadOnlyList<Transaction> orders,
        IReadOnlySet<string> tradedItems,
        int undercutCopper,
        SessionReportAccumulator reportAccumulator,
        CancellationToken cancellationToken)
    {
        try
        {
            var now = _utcNow();
            if (!orders.Any(order => order.Type == TransactionType.Sell && now - order.Created >= SellRelistPolicy.MinListingAge))
            {
                return;
            }

            var paid = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, cost) in await _store.GetOrderedItemCostsAsync(now.AddDays(-30), cancellationToken).ConfigureAwait(false))
            {
                paid[name.Trim()] = Math.Max(cost, paid.GetValueOrDefault(name.Trim()));
            }

            var picks = SellRelistPolicy.Select(orders, paid, tradedItems, _relistedAtUtc, Math.Max(1, undercutCopper), now, maxPerPass: 2);
            if (picks.Count == 0)
            {
                return;
            }

            foreach (var name in await liveDriver.CancelSellListingsAsync(picks, cancellationToken).ConfigureAwait(false))
            {
                _relistedAtUtc[name] = now;
                reportAccumulator.RecordTrade(now, "Relist", name, 1, 0, "Canceled", "Undercut listing cancelled for relisting; it returns via the Delivery Box.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("Relist pass skipped ({Message}).", ex.Message);
        }
    }

    /// <summary>
    /// Start the re-scan clock at the scanned list's file time, so a list saved hours or days ago is refreshed on the
    /// first cycle instead of trading on stale prices until the first interval passes.
    /// </summary>
    internal static DateTimeOffset InitialRescanClock(TradingReplayRequest request, DateTimeOffset nowUtc)
    {
        if (request.AutoRescanFilter is null || request.AutoRescanIntervalMinutes <= 0 ||
            string.IsNullOrWhiteSpace(request.CandidatesPath) || !File.Exists(request.CandidatesPath))
        {
            return nowUtc;
        }

        var written = new DateTimeOffset(File.GetLastWriteTimeUtc(request.CandidatesPath), TimeSpan.Zero);
        return written < nowUtc ? written : nowUtc;
    }

    internal static IReadOnlyList<ItemCandidate> MergeWithCustomList(IReadOnlyList<ItemCandidate> custom, IReadOnlyList<ItemCandidate> scanned)
    {
        if (custom.Count == 0)
        {
            return scanned;
        }

        var customNames = custom
            .Where(c => !string.IsNullOrWhiteSpace(c.ItemName))
            .Select(c => ItemNameNormalizer.Normalize(c.ItemName))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return custom
            .Concat(scanned.Where(c => !customNames.Contains(ItemNameNormalizer.Normalize(c.ItemName))))
            .ToArray();
    }

    /// <summary>One line for the Session page: list size and when the next market re-scan happens (or why it is off).</summary>
    internal static string DescribeMarketList(TradingReplayRequest request, int listCount, int scannedCount, DateTimeOffset lastRescanUtc, DateTimeOffset nowUtc)
    {
        if (request.AutoRescanFilter is null || request.AutoRescanIntervalMinutes <= 0)
        {
            return $"{listCount} items · re-scan off ({(request.AutoRescanIntervalMinutes <= 0 && request.AutoRescanFilter is not null ? "0 h" : "list is not the scanner's")})";
        }

        var due = lastRescanUtc.AddMinutes(request.AutoRescanIntervalMinutes) - nowUtc;
        var next = due <= TimeSpan.Zero
            ? "re-scan now"
            : due.TotalHours >= 1 ? $"re-scan in {(int)due.TotalHours}h {due.Minutes:00}m" : $"re-scan in {Math.Max(1, (int)Math.Ceiling(due.TotalMinutes))}m";
        return $"{listCount} items ({scannedCount} scanned) · {next}";
    }

    internal static string DescribePortfolio(TradingReplayRequest request, PortfolioGuidance? guidance, string? syncNote)
    {
        if (!request.UsePortfolioIntelligence)
        {
            return "History off";
        }

        if (guidance is null)
        {
            return "History: loading";
        }

        var boosts = guidance.ByItemName.Values.Count(a => !a.ShouldSkip && a.ScoreMultiplier > 1.0);
        var losers = guidance.LosingItems?.Count ?? 0;
        var stacks = guidance.ByItemName.Values.Count(a => a.ShouldSkip && a.Reason?.Contains("sell stack", StringComparison.OrdinalIgnoreCase) == true);
        var text = $"History: {boosts} boosted · {losers} skipped (lost money)";
        if (stacks > 0) text += $" · {stacks} stacked";
        return syncNote is null ? text : $"{text} · {syncNote}";
    }

    /// <summary>Recovery may switch parked character at most once per <see cref="MinTimeBetweenRecoverySwaps"/>.</summary>
    private bool CanSwapCharacterForRecovery(DateTimeOffset nowUtc) => nowUtc - _lastCharacterSwapUtc >= MinTimeBetweenRecoverySwaps;

    private async Task SyncOwnedFlipInventoryFromApiAsync(
        Gw2TradingPostUiDriver? liveDriver,
        EffectiveBalanceState effectiveBalance,
        IReadOnlySet<string> candidateNameSet,
        IReadOnlyList<Gw2LedgerTransaction> ledgerHistory,
        BlacklistMatcher blacklistMatcher,
        PendingSellInventoryTracker pendingSellTracker,
        SessionReportAccumulator reportAccumulator,
        Func<DateTimeOffset> getLastSyncUtc,
        Action<DateTimeOffset> setLastSyncUtc,
        Func<long?> getLastHistoryBuyId,
        Action<long?> setLastHistoryBuyId,
        CancellationToken cancellationToken)
    {
        if (_marketData is not Gw2OfficialDataProvider officialData)
        {
            return;
        }

        var now = _utcNow();
        if (getLastSyncUtc() != DateTimeOffset.MinValue && (now - getLastSyncUtc()) < TimeSpan.FromMinutes(2))
        {
            return;
        }

        setLastSyncUtc(now);
        // Only the shopping list and items Mmopal itself ordered. The synced buy history holds every TP purchase on
        // the account, including personal ones (an old "Holographic Super Cheese" was being put up for sale).
        var allowedFlipNames = new HashSet<string>(candidateNameSet, StringComparer.OrdinalIgnoreCase);

        // Items Mmopal itself ordered are flip items even after a re-scan drops them from the shopping list and
        // before the buy history is synced; without this, fills from an older list were never listed.
        try
        {
            foreach (var name in await _store.GetOrderedItemNamesAsync(now.AddDays(-30), cancellationToken).ConfigureAwait(false))
            {
                allowedFlipNames.Add(name.Trim());
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not read previously ordered items.");
        }

        try
        {
            var (invAvailable, bagItemIds) = await officialData.GetUnboundInventoryItemIdsAsync(cancellationToken).ConfigureAwait(false);
            if (!invAvailable && !_warnedInventoryUnavailable)
            {
                _warnedInventoryUnavailable = true;
                _logger.LogWarning("Auto-list cannot read your bags: the active API key needs the 'characters' and 'inventories' permissions. Bought items are only listed right after a Delivery Box pickup.");
            }

            if (invAvailable && bagItemIds.Count > 0 && allowedFlipNames.Count > 0)
            {
                var itemMeta = await officialData.ResolveItemMetadataAsync(bagItemIds, _ledgerStore, cancellationToken).ConfigureAwait(false);
                var confirmedFlipItems = itemMeta.Values
                    .Where(info => !string.IsNullOrWhiteSpace(info.Name) &&
                                   allowedFlipNames.Contains(info.Name.Trim()) &&
                                   !blacklistMatcher.IsBlocked(info.Name))
                    .Select(info => info.Name.Trim())
                    .ToArray();
                if (confirmedFlipItems.Length > 0)
                {
                    pendingSellTracker.SyncFromCharacterInventory(confirmedFlipItems, now);
                    var seen = string.Join(", ", confirmedFlipItems.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
                    if (!string.Equals(seen, _lastLoggedBagFlipItems, StringComparison.Ordinal))
                    {
                        _lastLoggedBagFlipItems = seen;
                        _logger.LogInformation("Bought flip items in bags, queued to list: {Items}", seen);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Bag inventory API sync skipped for this cycle.");
        }

        try
        {
            var (highestBuyId, newFills) = await officialData.GetRecentHistoryBuyItemIdsAsync(
                getLastHistoryBuyId(),
                cancellationToken).ConfigureAwait(false);
            setLastHistoryBuyId(highestBuyId);

            if (newFills.Count > 0 && allowedFlipNames.Count > 0)
            {
                var fillMeta = await officialData.ResolveItemMetadataAsync(
                    newFills.Select(f => f.ItemId),
                    _ledgerStore,
                    cancellationToken).ConfigureAwait(false);
                var filledFlipNames = fillMeta.Values
                    .Where(info => !string.IsNullOrWhiteSpace(info.Name) &&
                                   allowedFlipNames.Contains(info.Name.Trim()) &&
                                   !blacklistMatcher.IsBlocked(info.Name))
                    .Select(info => info.Name.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                _buyCompetition.RecordFills(filledFlipNames);
                if (filledFlipNames.Length > 0)
                {
                    var pickedUp = false;
                    if (liveDriver is not null)
                    {
                        var deliveryResult = await CollectSmartDeliveryAsync(
                            liveDriver,
                            effectiveBalance,
                            candidateNameSet,
                            blacklistMatcher,
                            pendingSellTracker,
                            reportAccumulator,
                            cancellationToken).ConfigureAwait(false);
                        pickedUp = deliveryResult.Collected;
                    }

                    if (pickedUp || liveDriver is null)
                    {
                        pendingSellTracker.RecordNewBuyFill(filledFlipNames, now);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Buy history watermark sync skipped for this cycle.");
        }
    }

    private async Task<string> FinalizeSessionOnCapAsync(
        string runId,
        RunMode mode,
        string profileName,
        TradingReplayRequest request,
        RunProfile profile,
        Gw2ClientSettings effectiveClientSettings,
        Gw2TradingPostUiDriver? liveDriver,
        EffectiveBalanceState effectiveBalance,
        IReadOnlySet<string> candidateNameSet,
        IReadOnlyList<Gw2LedgerTransaction> ledgerHistory,
        BlacklistMatcher blacklistMatcher,
        PendingSellInventoryTracker pendingSellTracker,
        SessionReportAccumulator reportAccumulator,
        Func<DateTimeOffset> getLastSyncUtc,
        Action<DateTimeOffset> setLastSyncUtc,
        Func<long?> getLastHistoryBuyId,
        Action<long?> setLastHistoryBuyId)
    {
        _logger.LogInformation("Max session duration cap reached; finalizing, rechecking, logging out, and stopping cleanly.");
        SetStatus(
            WorkerState.Stopping,
            BotKind.TradingPost,
            mode,
            profileName,
            "Finalizing session cap",
            "Max session duration reached; rechecking Delivery Box/listings, logging out, and closing Guild Wars 2.");

        using var finalizeCts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var ct = finalizeCts.Token;

        if (liveDriver is not null)
        {
            try
            {
                if (request.EnableInventorySellPass)
                {
                    setLastSyncUtc(DateTimeOffset.MinValue);
                    await SyncOwnedFlipInventoryFromApiAsync(
                        liveDriver,
                        effectiveBalance,
                        candidateNameSet,
                        ledgerHistory,
                        blacklistMatcher,
                        pendingSellTracker,
                        reportAccumulator,
                        getLastSyncUtc,
                        setLastSyncUtc,
                        getLastHistoryBuyId,
                        setLastHistoryBuyId,
                        ct).ConfigureAwait(false);
                }

                await CollectSmartDeliveryAsync(
                    liveDriver,
                    effectiveBalance,
                    candidateNameSet,
                    blacklistMatcher,
                    pendingSellTracker,
                    reportAccumulator,
                    ct).ConfigureAwait(false);

                if (request.EnableInventorySellPass)
                {
                    var finalTargets = pendingSellTracker.GetReadyTargets(_utcNow(), Math.Max(1, profile.MaxOverbidUpdatesPerPass));
                    IReadOnlyDictionary<string, SellPricing> finalPricing;
                    (finalTargets, finalPricing) = await KeepProfitableSellTargetsAsync(finalTargets, request.EffectiveCandidates, request.UndercutBufferMinCopper, ct).ConfigureAwait(false);
                    if (finalTargets.Count > 0)
                    {
                        var sellResults = await liveDriver.SellInventoryPassDetailedAsync(
                            finalTargets,
                            Math.Max(1, profile.MaxOverbidUpdatesPerPass),
                            request.UndercutBufferMinCopper,
                            request.UndercutBufferMaxCopper,
                            ct,
                            finalPricing).ConfigureAwait(false);
                        pendingSellTracker.RecordSellPassResults(sellResults, _utcNow());
                        foreach (var s in sellResults.Where(r => r.Outcome == SellPassItemOutcome.Listed))
                        {
                            reportAccumulator.RecordTrade(_utcNow(), "Sell Listing", s.ItemName, 1, 0, "Completed", "Listed during final cap recheck.");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Final delivery/listing recheck interrupted before logout.");
            }
        }

        if (_marketData is not null)
        {
            try
            {
                var finalBal = await _marketData.GetBalanceAsync(ct).ConfigureAwait(false);
                var finalTx = await _marketData.GetTransactionsAsync(ct).ConfigureAwait(false);
                reportAccumulator.RecordWalletSnapshot(finalBal?.Copper, finalTx.Count);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Final API wallet snapshot unavailable during cap shutdown.");
            }
        }

        var gameClosedNote = "Game left open per settings.";
        if (effectiveClientSettings.AutoCloseGameOnSessionCap &&
            (liveDriver is not null || string.Equals(request.Source, "screen", StringComparison.OrdinalIgnoreCase)))
        {
            if (liveDriver is not null)
            {
                try
                {
                    var (loggedOut, logoutMsg) = await liveDriver.GracefulLogoutAndCloseGameAsync(ct).ConfigureAwait(false);
                    _logger.LogInformation("In-game cap logout ({LoggedOut}): {Message}", loggedOut, logoutMsg);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "In-game logout step skipped before process close.");
                }
            }

            try
            {
                var closeResult = await _clientPreparer.CloseGameAsync(effectiveClientSettings, CancellationToken.None).ConfigureAwait(false);
                gameClosedNote = closeResult.Message;
            }
            catch (Exception ex)
            {
                gameClosedNote = $"Close game attempt failed: {ex.Message}";
                reportAccumulator.RecordIncident(_utcNow(), "Warning", "Game Close", gameClosedNote, recovered: false);
            }
        }

        await _store.CompleteRunAsync(runId, WorkerState.Stopped.ToString(), CancellationToken.None).ConfigureAwait(false);
        var stoppedSummary = $"Max session cap reached — finalized, rechecked, and stopped cleanly. ({gameClosedNote})";
        SetStatus(
            WorkerState.Stopped,
            BotKind.TradingPost,
            mode,
            profileName,
            "Session cap reached",
            stoppedSummary);
        return stoppedSummary;
    }

    private async Task<IReadOnlyList<ItemCandidate>> RefreshCycleCandidatesAsync(
        IReadOnlyList<ItemCandidate> baselineCandidates,
        Dictionary<string, int> resolvedItemIdCache,
        LiveCandidateProfitRefresher refresher,
        ProfitabilityPolicy policy,
        int overbidMinCopper,
        int undercutMinCopper,
        PortfolioGuidance? portfolioGuidance,
        CancellationToken cancellationToken)
    {
        if (baselineCandidates.Count == 0)
        {
            return baselineCandidates;
        }

        if (_marketData is not Gw2OfficialDataProvider officialData)
        {
            if (portfolioGuidance is null)
            {
                return baselineCandidates;
            }

            var offlineResult = refresher.Refresh(
                baselineCandidates,
                new Dictionary<int, Gw2LivePriceQuote>(),
                policy,
                overbidMinCopper,
                undercutMinCopper,
                portfolioGuidance);
            return offlineResult.Candidates;
        }

        try
        {
            var unresolvedNames = baselineCandidates
                .Where(c => c.ItemId is null && !string.IsNullOrWhiteSpace(c.ItemName) && !resolvedItemIdCache.ContainsKey(c.ItemName.Trim()))
                .Select(c => c.ItemName.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (unresolvedNames.Length > 0 && _ledgerStore is not null)
            {
                var found = await _ledgerStore.GetItemIdsByNamesAsync(unresolvedNames, cancellationToken).ConfigureAwait(false);
                foreach (var (name, id) in found)
                {
                    resolvedItemIdCache[name] = id;
                }
            }

            var enriched = new ItemCandidate[baselineCandidates.Count];
            var itemIds = new List<int>(baselineCandidates.Count);
            for (var i = 0; i < baselineCandidates.Count; i++)
            {
                var c = baselineCandidates[i];
                if (c.ItemId is { } id && id > 0)
                {
                    enriched[i] = c;
                    itemIds.Add(id);
                }
                else if (!string.IsNullOrWhiteSpace(c.ItemName) && resolvedItemIdCache.TryGetValue(c.ItemName.Trim(), out var cachedId) && cachedId > 0)
                {
                    enriched[i] = c with { ItemId = cachedId };
                    itemIds.Add(cachedId);
                }
                else
                {
                    enriched[i] = c;
                }
            }

            var distinctIds = itemIds.Distinct().ToArray();
            if (distinctIds.Length == 0)
            {
                return portfolioGuidance is null
                    ? baselineCandidates
                    : refresher.Refresh(baselineCandidates, new Dictionary<int, Gw2LivePriceQuote>(), policy, overbidMinCopper, undercutMinCopper, portfolioGuidance).Candidates;
            }

            var quotes = await officialData.GetLiveMarketQuotesAsync(distinctIds, cancellationToken).ConfigureAwait(false);
            var result = refresher.Refresh(enriched, quotes, policy, overbidMinCopper, undercutMinCopper, portfolioGuidance);
            var cycleInfo = Gw2MarketCycle.Describe(_utcNow());
            var portfolioNote = portfolioGuidance is not null && (result.PortfolioSkippedCount > 0 || result.PortfolioBoostedCount > 0)
                ? $", {result.PortfolioBoostedCount} winner-boosted, {result.PortfolioSkippedCount} portfolio-guarded"
                : string.Empty;
            _logger.LogInformation(
                "GW2 Live Pulse ({Phase}): {Refreshed} profitable, {Dropped} spread-collapsed dropped, {Unquoted} unquoted{PortfolioNote}. {Top}",
                cycleInfo.PhaseName,
                result.RefreshedCount,
                result.DroppedCollapsedCount,
                result.RetainedUnquotedCount,
                portfolioNote,
                result.TopPickSummary);
            return result.Candidates;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Live API profit refresh failed; using cached candidate spreads for this cycle.");
            return baselineCandidates;
        }
    }

    private void HaltWithNeedsAttention(
        RunMode mode,
        string profileName,
        string source,
        string lastAction,
        string message)
    {
        SetStatus(
            WorkerState.NeedsAttention,
            BotKind.TradingPost,
            mode,
            profileName,
            lastAction,
            message);
        Emit(new WorkerEvent(
            WorkerEventTypes.Health,
            JsonSerializer.Serialize(new
            {
                state = WorkerState.NeedsAttention.ToString(),
                succeeded = false,
                source,
                error = message,
                timestamp = _utcNow()
            },
            PayloadJsonOptions)));
    }

    private async Task<(bool Recovered, string Message)> TryRecoverOrRestartLiveClientAsync(
        Gw2TradingPostUiDriver liveDriver,
        Gw2ClientSettings settings,
        RunMode mode,
        string profileName,
        string initialError,
        int maxRecoveryRetries,
        int maxGameRestarts,
        Func<int> getGameRestartsUsed,
        Action<int> setGameRestartsUsed,
        CancellationToken cancellationToken)
    {
        var lastError = initialError;
        var isLiveTemplates = _liveTemplateMatcher is null;

        if (liveDriver.ServerDisconnectSeenUtc is { } disconnectedAt && _utcNow() - disconnectedAt < TimeSpan.FromMinutes(10))
        {
            // GW2 asks to wait a few minutes after a server disconnect; logging straight back in risks another kick.
            liveDriver.ServerDisconnectSeenUtc = null;
            var wait = isLiveTemplates ? ServerDisconnectCooldown : TimeSpan.FromMilliseconds(5);
            _logger.LogWarning("Disconnected by the server; waiting {Minutes} minutes before reconnecting.", (int)wait.TotalMinutes);
            SetStatusIfNotPaused(WorkerState.Recovering, BotKind.TradingPost, mode, profileName, "Waiting after disconnect", $"Server disconnect; reconnecting in {(int)wait.TotalMinutes} min.");
            await WaitIntervalOrPauseAsync(wait, cancellationToken).ConfigureAwait(false);
            if (IsPauseRequested)
            {
                return (false, "Paused by operator while waiting after a server disconnect.");
            }
        }

        for (var retry = 1; retry <= maxRecoveryRetries; retry++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsPauseRequested)
            {
                return (false, "Paused by operator during session recovery.");
            }

            var message = $"Recovering GW2 session (retry {retry}/{maxRecoveryRetries}): {initialError}";
            _logger.LogWarning("{Message}", message);
            SetStatusIfNotPaused(
                WorkerState.Recovering,
                BotKind.TradingPost,
                mode,
                profileName,
                $"Recovering ({retry}/{maxRecoveryRetries})",
                message);

            var delay = isLiveTemplates
                ? TimeSpan.FromSeconds(Math.Min(6, 2 * retry))
                : TimeSpan.FromMilliseconds(5);
            await WaitIntervalOrPauseAsync(delay, cancellationToken).ConfigureAwait(false);
            if (IsPauseRequested)
            {
                return (false, "Paused by operator during session recovery.");
            }

            bool ok;
            string recoverMsg;
            if (retry == 1 && liveDriver.LastTradingPostOpenFailed && settings.ParkedCharacterCount > 1 &&
                CanSwapCharacterForRecovery(_utcNow()))
            {
                _lastCharacterSwapUtc = _utcNow();
                // Every key was tried on this character (e.g. a bank NPC next to the trader keeps winning F), so
                // retrying in place or restarting into the same character repeats the failure. Switch characters.
                _logger.LogWarning("Trading Post would not open on this character; switching to another parked character first.");
                (ok, recoverMsg) = await liveDriver.RotateParkedCharacterAsync(
                    settings.RandomizeCharacterSelection,
                    settings.ParkedCharacterCount,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                (ok, recoverMsg) = await liveDriver.TryRecoverTradingSessionAsync(cancellationToken).ConfigureAwait(false);
            }

            if (ok)
            {
                _sessionSellTracker?.CharacterChanged(_utcNow());
                var okText = $"Recovered GW2 Trading Post on retry {retry}/{maxRecoveryRetries}: {recoverMsg}";
                _logger.LogInformation("{Message}", okText);
                return (true, okText);
            }

            lastError = recoverMsg;
        }

        var restartsUsed = getGameRestartsUsed();
        if (restartsUsed < maxGameRestarts)
        {
            restartsUsed++;
            setGameRestartsUsed(restartsUsed);
            var restartNotice = $"In-place recovery exhausted ({maxRecoveryRetries} retries); restarting Guild Wars 2 (restart {restartsUsed}/{maxGameRestarts}).";
            _logger.LogWarning("{Message}", restartNotice);
            SetStatusIfNotPaused(
                WorkerState.Recovering,
                BotKind.TradingPost,
                mode,
                profileName,
                $"Restarting GW2 ({restartsUsed}/{maxGameRestarts})",
                restartNotice);

            var restarted = await _clientPreparer.RestartAsync(settings, cancellationToken).ConfigureAwait(false);
            if (IsPauseRequested)
            {
                return (false, "Paused by operator during GW2 restart.");
            }

            if (restarted.WindowReady)
            {
                _ = await liveDriver.TryAdvanceLauncherOrCharacterSelectAsync(cancellationToken).ConfigureAwait(false);
                var (reopened, reopenMsg) = await liveDriver.EnsureTradingPostOpenAsync(cancellationToken).ConfigureAwait(false);
                if (reopened)
                {
                    _sessionSellTracker?.CharacterChanged(_utcNow());
                    _deliveryLockedSinceUtc = null;
                    var recoveredAfterRestart = $"Recovered session after restarting Guild Wars 2 ({restartsUsed}/{maxGameRestarts}).";
                    _logger.LogInformation("{Message}", recoveredAfterRestart);
                    return (true, recoveredAfterRestart);
                }

                lastError = reopenMsg;
            }
            else
            {
                lastError = restarted.Message;
            }
        }

        return (false, $"Session halted after {maxRecoveryRetries} recovery retry(s) and {restartsUsed}/{maxGameRestarts} game restart(s): {lastError}");
    }

    private void Emit(WorkerEvent workerEvent) => EventRaised?.Invoke(this, workerEvent);

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
