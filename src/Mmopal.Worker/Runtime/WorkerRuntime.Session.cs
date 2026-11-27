using System.Text.Json;
using Microsoft.Extensions.Logging;
using Mmopal.Contracts;
using Mmopal.Gw2;
using Mmopal.Infrastructure;
using Mmopal.Native;

namespace Mmopal.Worker;

/// <summary>Starting a trading session (readers, driver, controller, schedules) and finishing it (session report).</summary>
public sealed partial class WorkerRuntime
{
    /// <summary>Everything one trading session works with and remembers between cycles.</summary>
    private sealed class TradingSession
    {
        // Fixed for the session.
        public required string RunId { get; init; }
        public required RunMode Mode { get; init; }
        public required string ProfileName { get; init; }
        public required TradingReplayRequest Request { get; init; }
        public required RunProfile Profile { get; init; }
        public required Gw2ClientSettings Client { get; init; }
        public required Gw2TradingPostUiDriver? LiveDriver { get; init; }
        public required TradingCycleController Controller { get; init; }
        public required ProfitabilityPolicy Policy { get; init; }
        public required TimeSpan Interval { get; init; }
        public required AfkCharacterSwapSchedule AfkSwap { get; init; }
        public required SessionDurationCap Cap { get; init; }
        public required IReadOnlyList<string> Blacklist { get; init; }
        public required BlacklistMatcher BlacklistMatcher { get; init; }
        public required PendingSellInventoryTracker SellQueue { get; init; }
        public required EffectiveBalanceState Balance { get; init; }
        public required SessionReportAccumulator Report { get; init; }
        public required TradingPostMarketReader? ObservationReader { get; init; }
        public required FileTradingObservationRecorder? Recorder { get; init; }
        public required long MinBalanceCopper { get; init; }
        public required int MaxRecoveryRetries { get; init; }
        public required int MaxGameRestarts { get; init; }
        public LiveCandidateProfitRefresher ProfitRefresher { get; } = new();
        public Dictionary<string, int> ResolvedItemIds { get; } = new(StringComparer.OrdinalIgnoreCase);

        // Shopping list (replaced by auto re-scans).
        public required IReadOnlyList<ItemCandidate> Candidates { get; set; }
        public required HashSet<string> CandidateNames { get; set; }
        public required DateTimeOffset LastRescanUtc { get; set; }
        public required int LastRescanCount { get; set; }

        // Trade history and account state.
        public DateTimeOffset LastLedgerSyncUtc { get; set; } = DateTimeOffset.MinValue;
        public string? LastLedgerSyncNote { get; set; }
        public IReadOnlyList<Gw2LedgerTransaction> LedgerHistory { get; set; } = Array.Empty<Gw2LedgerTransaction>();
        public DateTimeOffset LedgerLoadedAtUtc { get; set; } = DateTimeOffset.MinValue;
        public required IReadOnlyList<Transaction> LastKnownActiveOrders { get; set; }
        public long? LastHistoryBuyId { get; set; }
        public DateTimeOffset LastInventoryApiSyncUtc { get; set; } = DateTimeOffset.MinValue;
        /// <summary>The bag read running next to the launch.</summary>
        public Task StockBeforeLaunch { get; set; } = Task.CompletedTask;
        /// <summary>Characters holding unsold flip items, most first (from the last bag sync).</summary>
        public IReadOnlyList<(string Name, long Items)> FlipStockByCharacter { get; set; } = [];
        /// <summary>Flip item names in each character's bags (from the last bag sync).</summary>
        public IReadOnlyDictionary<string, IReadOnlySet<string>> FlipItemsByCharacter { get; set; } =
            new Dictionary<string, IReadOnlySet<string>>();
        /// <summary>Units of each flip item in each character's bags (from the last bag sync).</summary>
        public IReadOnlyDictionary<string, IReadOnlyDictionary<string, long>> FlipUnitsByCharacter { get; set; } =
            new Dictionary<string, IReadOnlyDictionary<string, long>>();
        /// <summary>Units of each item Mmopal itself still holds (bought by its orders, not sold or listed); null until read.</summary>
        public IReadOnlyDictionary<string, long>? HeldByMmopal { get; set; }
        /// <summary>Items already logged as holding the player's own copies (logged once per session).</summary>
        public HashSet<string> OwnCopiesLogged { get; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Unsold units of each flip item across all bags (from the last bag sync).</summary>
        public IReadOnlyDictionary<string, long> HeldFlipItems { get; set; } = new Dictionary<string, long>();
        /// <summary>Candidates skipped last cycle because their item is still unsold in bags.</summary>
        public int BuysWaitingOnStock { get; set; }

        // Recovery budget.
        public int GameRestartsUsed { get; set; }
        public DateTimeOffset LastGameRestartUtc { get; set; } = DateTimeOffset.MinValue;
        public int ConsecutiveFailedCycles { get; set; }
        public DateTimeOffset LastFrozenReopenUtc { get; set; } = DateTimeOffset.MinValue;

        // Items proven to fill and sell (larger orders), worked out again every half hour.
        public IReadOnlySet<string> ProvenItems { get; set; } = new HashSet<string>();
        public DateTimeOffset ProvenItemsAtUtc { get; set; } = DateTimeOffset.MinValue;

        public SessionMeasurements Measured { get; } = new();

        /// <summary>Faster cycles while items wait and gold is idle.</summary>
        public PaceBoost Boost { get; } = new();
        public int PerPass => Boost.PerPass(Math.Max(1, Profile.MaxOverbidUpdatesPerPass));
        public bool GoldCapReached { get; set; }

        // How the session ended (for the report).
        public bool CapReached { get; set; }
        public string Outcome { get; set; } = "Completed";
        public string OutcomeSummary { get; set; } = "Trading session completed.";

        public void EndWith(string outcome, string summary)
        {
            Outcome = outcome;
            OutcomeSummary = summary;
        }
    }

    private void SetGameRestartsUsed(TradingSession session, int used)
    {
        session.GameRestartsUsed = used;
        session.LastGameRestartUtc = _utcNow();
    }

    /// <summary>
    /// Builds the session: market reader (screen, fixture or replay), live driver and executor, cycle controller,
    /// schedules and report. Returns null when the GW2 window could not be prepared (already reported).
    /// </summary>
    private async Task<TradingSession?> StartTradingSessionAsync(
        string runId,
        RunMode mode,
        string profileName,
        TradingReplayRequest request,
        CancellationToken cancellationToken)
    {
        var profile = LoadProfile(profileName);
        var effectiveClientSettings = request.ClientSettings ?? new Gw2ClientSettings();
        _activeClientSettings = effectiveClientSettings;
        _lastDeliveryUnlockAttemptUtc = DateTimeOffset.MinValue;
        _lastCharacterSwapUtc = DateTimeOffset.MinValue;
        _buyCompetition = new BuyCompetitionTracker();
        await PauseYesterdaysBotWarsAsync(mode, cancellationToken).ConfigureAwait(false);
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
                    return null;
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
                ? new ObserveActionExecutor()
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
        var sessionCap = new SessionDurationCap(effectiveClientSettings.MaxSessionDurationMinutes, _utcNow());
        var pendingSellTracker = new PendingSellInventoryTracker();
        _sessionSellTracker = pendingSellTracker;

        _logger.LogInformation(
            "Auto-list bought flip items from bags: {State}.",
            request.EnableInventorySellPass ? "on" : "off (tick it on the Session page to sell what the bot buys)");

        return new TradingSession
        {
            RunId = runId,
            Mode = mode,
            ProfileName = profileName,
            Request = request,
            Profile = profile,
            Client = effectiveClientSettings,
            LiveDriver = liveDriver,
            Controller = controller,
            Policy = request.Policy ?? new ProfitabilityPolicy(),
            Interval = TimeSpan.FromSeconds(Math.Max(2, profile.OverbidScanIntervalSeconds)),
            AfkSwap = new AfkCharacterSwapSchedule(
                effectiveClientSettings.SwapCharacterIntervalMinutes,
                effectiveClientSettings.SwapCharacterJitterMinutes,
                effectiveClientSettings.RandomizeCharacterSelection),
            Cap = sessionCap,
            Blacklist = blacklist,
            BlacklistMatcher = new BlacklistMatcher(blacklist),
            SellQueue = pendingSellTracker,
            Balance = effectiveBalance,
            Report = new SessionReportAccumulator(
                runId,
                request.ActiveAccountName,
                BotKind.TradingPost,
                mode,
                request.Source,
                profileName,
                sessionCap.StartedAt),
            ObservationReader = observationMarketReader,
            Recorder = recorder,
            MinBalanceCopper = minBalanceCopper,
            MaxRecoveryRetries = Math.Clamp(effectiveClientSettings.MaxRecoveryRetries, 0, 10),
            MaxGameRestarts = effectiveClientSettings.AutoRestartGameOnFailure
                ? Math.Clamp(effectiveClientSettings.MaxGameRestartsPerSession, 0, 5)
                : 0,
            Candidates = candidates,
            CandidateNames = CandidateNameSet(candidates),
            // A scanned list older than the re-scan interval (for example yesterday's) is refreshed on the first cycle.
            LastRescanUtc = InitialRescanClock(request, _utcNow()),
            LastRescanCount = candidates.Count,
            LastKnownActiveOrders = request.EffectiveTransactions
        };
    }

    private static HashSet<string> CandidateNameSet(IEnumerable<ItemCandidate> candidates) =>
        candidates
            .Where(c => !string.IsNullOrWhiteSpace(c.ItemName))
            .Select(c => c.ItemName.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Closes the observation recorder, then builds, saves (per the report policy) and announces the session report.</summary>
    private async Task FinishTradingSessionAsync(TradingSession session)
    {
        if (session.Recorder is not null)
        {
            await session.Recorder.DisposeAsync();
        }

        try
        {
            var finalReport = await AddMeasurementsAsync(session, session.Report.Build(_utcNow(), session.Outcome, session.OutcomeSummary)).ConfigureAwait(false);
            var policySettings = session.Request.ReportSettings ?? new SessionReportPolicySettings();
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

            var client = session.Client;
            var notification = new SessionReportNotification(
                Report: finalReport,
                Saved: shouldSaveReport,
                CapReached: session.CapReached,
                AutoCloseApp: session.CapReached && client.AutoCloseAppOnSessionCap,
                ShutdownPc: session.CapReached && client.ShutdownPcOnSessionCap,
                ShutdownCountdownSeconds: Math.Clamp(client.PcShutdownCountdownSeconds, 10, 600));
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
