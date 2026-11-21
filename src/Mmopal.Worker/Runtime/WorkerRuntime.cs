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
