using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Mmopal.Contracts;
using Mmopal.Gw2;
using Mmopal.Infrastructure;
using Mmopal.Native;
using Mmopal.Worker;
using Xunit;

namespace Mmopal.Domain.Tests;

/// <summary>The worker runtime: armed/disarmed live mode, recovery, and the working balance.</summary>
public sealed partial class LiveTradingExecutorTests
{
    [Fact]
    public async Task WorkerRuntime_LiveModeWithoutArmLiveInput_HaltsInNeedsAttention_WithoutInputCalls()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "mmopal-live-disarmed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var store = new CheckpointStore(Path.Combine(tempRoot, "mmopal.db"));
            await store.InitializeAsync();

            var fakeInput = new FakeInputController();
            var fakeScreens = new FakeScreenshotProvider();
            var fakeTemplates = new FakeTemplateMatcher();
            var fakeWindows = new FakeWindowController();

            var runtime = new WorkerRuntime(
                store,
                NullLogger.Instance,
                liveInputController: fakeInput,
                liveScreenshots: fakeScreens,
                liveTemplateMatcher: fakeTemplates,
                liveWindowController: fakeWindows);

            var attentionTcs = new TaskCompletionSource<WorkerStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
            runtime.EventRaised += (_, workerEvent) =>
            {
                if (workerEvent.Type == WorkerEventTypes.Status)
                {
                    var status = JsonSerializer.Deserialize<WorkerStatus>(workerEvent.Payload);
                    if (status?.State == WorkerState.NeedsAttention)
                    {
                        attentionTcs.TrySetResult(status);
                    }
                }
            };

            var payload = JsonSerializer.Serialize(new TradingReplayRequest(
                Candidates: [new ItemCandidate("Glob of Ectoplasm", Quantity: 1, BuyPriceCopper: 2_000, SellPriceCopper: 4_000)],
                Balance: new AccountBalance(100_000),
                Source: "replay",
                ArmLiveInput: false),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));

            await runtime.HandleAsync(new WorkerCommand(WorkerCommandTypes.Start, "req-disarmed", BotKind.TradingPost, RunMode.Live, "balanced", payload));
            var attentionStatus = await attentionTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await runtime.HandleAsync(new WorkerCommand(WorkerCommandTypes.Stop));

            Assert.Equal(WorkerState.NeedsAttention, attentionStatus.State);
            Assert.Contains("not armed", attentionStatus.LastAction ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, fakeInput.Clicks.Count + fakeInput.KeyPresses.Count + fakeInput.TextsSent.Count);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task WorkerRuntime_LiveModeWithArmLiveInput_ExecutesMockedCycleAndEmitsHealth()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "mmopal-live-armed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var store = new CheckpointStore(Path.Combine(tempRoot, "mmopal.db"));
            await store.InitializeAsync();

            var fakeInput = new FakeInputController();
            var fakeScreens = new FakeScreenshotProvider();
            var fakeTemplates = new FakeTemplateMatcher(new Dictionary<string, TemplateMatch>(StringComparer.OrdinalIgnoreCase)
            {
                ["tradingpost_tab.png"] = new(90, 20, 80, 24, 0.95m),
                ["buy_items_tab_open.png"] = new(210, 45, 70, 22, 0.95m),
                ["search.png"] = new(60, 110, 120, 24, 0.95m),
                ["item.png"] = new(220, 170, 180, 40, 0.95m),
                ["currentBuyers.png"] = new(540, 230, 100, 20, 0.95m),
                ["quantity.png"] = new(540, 310, 60, 20, 0.95m),
                ["arrow_up.png"] = new(700, 345, 16, 16, 0.95m),
                ["placeOrder.png"] = new(610, 410, 110, 30, 0.95m)
            });
            var fakeWindows = new FakeWindowController();

            var runtime = new WorkerRuntime(
                store,
                NullLogger.Instance,
                liveInputController: fakeInput,
                liveScreenshots: fakeScreens,
                liveTemplateMatcher: fakeTemplates,
                liveWindowController: fakeWindows);

            var healthTcs = new TaskCompletionSource<WorkerEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            runtime.EventRaised += (_, workerEvent) =>
            {
                if (workerEvent.Type == WorkerEventTypes.Health)
                {
                    healthTcs.TrySetResult(workerEvent);
                }
            };

            var payload = JsonSerializer.Serialize(new TradingReplayRequest(
                Candidates: [new ItemCandidate("Glob of Ectoplasm", Quantity: 2, BuyPriceCopper: 2_000, SellPriceCopper: 4_000)],
                Transactions: Array.Empty<Transaction>(),
                Balance: new AccountBalance(100_000),
                ObservedAt: DateTimeOffset.UtcNow,
                Source: "replay",
                ClientSettings: new Gw2ClientSettings(
                    UiLoadWaitMs: 40,
                    ActionSettleDelayMs: 0,
                    SwapCharacterIntervalMinutes: null),
                ArmLiveInput: true,
                EnableInventorySellPass: false,
                EnableOutbidBuyRefresh: false),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));

            await runtime.HandleAsync(new WorkerCommand(WorkerCommandTypes.Start, "req-armed", BotKind.TradingPost, RunMode.Live, "balanced", payload));
            var health = await healthTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await runtime.HandleAsync(new WorkerCommand(WorkerCommandTypes.Stop));

            using var doc = JsonDocument.Parse(health.Payload);
            var root = doc.RootElement;
            Assert.True(root.GetProperty("succeeded").GetBoolean());
            Assert.Equal(1, root.GetProperty("decisions").GetInt32());
            Assert.Equal(1, root.GetProperty("actions").GetInt32());
            Assert.Contains("Glob of Ectoplasm", fakeInput.TextsSent);
            Assert.Contains((665, 425), fakeInput.Clicks);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void EffectiveBalanceState_DoesNotRecollectTheSameCoinsAndItemsWhileTheApiLags()
    {
        var state = new WorkerRuntime.EffectiveBalanceState();
        var t0 = new DateTimeOffset(2026, 11, 20, 8, 0, 0, TimeSpan.Zero);

        // Seen live: "24g 83s 06c coins, 1 item" collected four times in a row.
        state.RecordTakeAllAttempt(deliveryCoinsCopper: 248_306, apiWalletBaselineCopper: 1_000_000, clickedTakeAll: true, nowUtc: t0, itemCount: 1);

        Assert.True(state.ShouldSkipStaleDeliveryCollection(248_306, 1, t0.AddSeconds(90)));
        Assert.False(state.ShouldSkipStaleDeliveryCollection(248_306, 2, t0.AddSeconds(90)));  // another item arrived
        Assert.False(state.ShouldSkipStaleDeliveryCollection(248_306, 1, t0.AddMinutes(6)));   // API window over
    }

    [Fact]
    public void EffectiveBalanceState_CreditsCollectedDeliveryCoinsForFiveMinutesUntilApiCatchesUp()
    {
        var state = new WorkerRuntime.EffectiveBalanceState();
        var t0 = new DateTimeOffset(2026, 11, 20, 2, 13, 0, TimeSpan.Zero);

        // API wallet still shows 24g 70s 31c (247_031c) right after collecting 17g 96s 55c (179_655c)
        state.RecordTakeAllAttempt(deliveryCoinsCopper: 179_655, apiWalletBaselineCopper: 247_031, clickedTakeAll: true, nowUtc: t0);
        Assert.True(state.ShouldSkipStaleDeliveryCollection(179_655, 0, t0.AddSeconds(20)));
        // The delivery API still lists the collected coins for up to its cache window: do not click Take All again.
        Assert.True(state.ShouldSkipStaleDeliveryCollection(179_655, 0, t0.AddSeconds(80)));
        Assert.False(state.ShouldSkipStaleDeliveryCollection(179_655, 0, t0.AddMinutes(5).AddSeconds(1)));
        // A different amount means new proceeds arrived: collect right away.
        Assert.False(state.ShouldSkipStaleDeliveryCollection(200_000, 0, t0.AddSeconds(80)));

        var staleSnapshot = new MarketSnapshot(Array.Empty<Transaction>(), new AccountBalance(247_031), t0.AddSeconds(10), "screen");
        var credited = state.ApplyEffectiveBalance(staleSnapshot, t0.AddSeconds(10));
        Assert.Equal(426_686, credited.Balance!.Copper); // 42g 66s 86c available above 26g floor!

        // Once 5 minutes elapse or wallet updates to >= 42g 66s 86c, temporary credit clears cleanly
        var syncedSnapshot = new MarketSnapshot(Array.Empty<Transaction>(), new AccountBalance(426_686), t0.AddMinutes(3), "screen");
        var afterSync = state.ApplyEffectiveBalance(syncedSnapshot, t0.AddMinutes(3));
        Assert.Equal(426_686, afterSync.Balance!.Copper);
        Assert.Equal(0, state.PendingCreditCoinsCopper);
    }

    [Fact]
    public void EffectiveBalanceState_RetriesFailedTakeAllAfterOneMinute()
    {
        var state = new WorkerRuntime.EffectiveBalanceState();
        var t0 = new DateTimeOffset(2026, 11, 20, 5, 58, 0, TimeSpan.Zero);

        state.RecordTakeAllAttempt(deliveryCoinsCopper: 60_294, apiWalletBaselineCopper: 3_286_250, clickedTakeAll: false, nowUtc: t0);

        Assert.True(state.ShouldSkipStaleDeliveryCollection(60_294, 0, t0.AddSeconds(30)));
        Assert.False(state.ShouldSkipStaleDeliveryCollection(60_294, 0, t0.AddSeconds(61)));
        Assert.Equal(0, state.PendingCreditCoinsCopper);
    }

    [Fact]
    public async Task WorkerRuntime_RecoversViaBoundedGameRestartBeforeHaltingWhenTpUnavailable()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "mmopal-live-restart-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var store = new CheckpointStore(Path.Combine(tempRoot, "mmopal.db"));
            await store.InitializeAsync();

            var fakeInput = new FakeInputController();
            var fakeScreens = new FakeScreenshotProvider();
            var fakeTemplates = new FakeTemplateMatcher();
            var fakeWindows = new FakeWindowController();
            var restartPreparer = new RestartTrackingPreparer();

            fakeTemplates.DynamicMatches["tradingpost_tab.png"] = () =>
                restartPreparer.RestartCount >= 1 ? new TemplateMatch(90, 20, 80, 24, 0.95m) : null;
            fakeTemplates.DynamicMatches["buy_items_tab_open.png"] = () =>
                restartPreparer.RestartCount >= 1 ? new TemplateMatch(210, 45, 70, 22, 0.95m) : null;
            fakeTemplates.DynamicMatches["search.png"] = () =>
                restartPreparer.RestartCount >= 1 ? new TemplateMatch(60, 110, 120, 24, 0.95m) : null;
            fakeTemplates.DynamicMatches["item.png"] = () =>
                restartPreparer.RestartCount >= 1 ? new TemplateMatch(220, 170, 180, 40, 0.95m) : null;
            fakeTemplates.DynamicMatches["currentBuyers.png"] = () =>
                restartPreparer.RestartCount >= 1 ? new TemplateMatch(540, 230, 100, 20, 0.95m) : null;
            fakeTemplates.DynamicMatches["placeOrder.png"] = () =>
                restartPreparer.RestartCount >= 1 ? new TemplateMatch(610, 410, 110, 30, 0.95m) : null;

            var runtime = new WorkerRuntime(
                store,
                NullLogger.Instance,
                clientPreparer: restartPreparer,
                liveInputController: fakeInput,
                liveScreenshots: fakeScreens,
                liveTemplateMatcher: fakeTemplates,
                liveWindowController: fakeWindows);

            var healthTcs = new TaskCompletionSource<WorkerEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            runtime.EventRaised += (_, workerEvent) =>
            {
                if (workerEvent.Type == WorkerEventTypes.Health)
                {
                    healthTcs.TrySetResult(workerEvent);
                }
            };

            var payload = JsonSerializer.Serialize(new TradingReplayRequest(
                Candidates: [new ItemCandidate("Glob of Ectoplasm", Quantity: 1, BuyPriceCopper: 2_000, SellPriceCopper: 4_000)],
                Transactions: Array.Empty<Transaction>(),
                Balance: new AccountBalance(100_000),
                ObservedAt: DateTimeOffset.UtcNow,
                Source: "replay",
                ClientSettings: new Gw2ClientSettings(
                    AutoPrepareOnScreenStart: false,
                    UiLoadWaitMs: 20,
                    ActionSettleDelayMs: 0,
                    SwapCharacterIntervalMinutes: null,
                    MaxRecoveryRetries: 1,
                    AutoRestartGameOnFailure: true,
                    MaxGameRestartsPerSession: 1),
                ArmLiveInput: true,
                EnableInventorySellPass: false,
                EnableOutbidBuyRefresh: false),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));

            await runtime.HandleAsync(new WorkerCommand(WorkerCommandTypes.Start, "req-restart", BotKind.TradingPost, RunMode.Live, "balanced", payload));
            var health = await healthTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await runtime.HandleAsync(new WorkerCommand(WorkerCommandTypes.Stop));

            Assert.Equal(1, restartPreparer.RestartCount);
            using var doc = JsonDocument.Parse(health.Payload);
            Assert.True(doc.RootElement.GetProperty("succeeded").GetBoolean());
            Assert.Contains("Glob of Ectoplasm", fakeInput.TextsSent);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }
}
