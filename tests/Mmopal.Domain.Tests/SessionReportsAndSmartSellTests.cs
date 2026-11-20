using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Mmopal.Contracts;
using Mmopal.Gw2;
using Mmopal.Infrastructure;
using Mmopal.Native;
using Mmopal.Worker;

namespace Mmopal.Domain.Tests;

public sealed class SessionReportsAndSmartSellTests
{
    private static readonly DateTimeOffset BaseTime = new(2026, 11, 20, 4, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SessionReportPolicy_Evaluates_Eligibility_Correctly()
    {
        var dryShort = BuildReport(runMode: "DryRun", durationSeconds: 1200, buys: 2, errors: 0, outcome: "Completed");
        Assert.False(SessionReportPolicy.ShouldSave(dryShort, new SessionReportPolicySettings()));
        Assert.True(SessionReportPolicy.ShouldSave(dryShort, new SessionReportPolicySettings(IncludeDryRunAndObserve: true)));

        var liveQuickNoise = BuildReport(runMode: "Live", durationSeconds: 30, buys: 0, errors: 0, outcome: "Stopped by User");
        Assert.False(SessionReportPolicy.ShouldSave(liveQuickNoise, new SessionReportPolicySettings()));

        var liveLongQuiet = BuildReport(runMode: "Live", durationSeconds: 600, buys: 0, errors: 0, outcome: "Completed");
        Assert.True(SessionReportPolicy.ShouldSave(liveLongQuiet, new SessionReportPolicySettings(MinDurationMinutes: 5)));

        var liveShortWithTrade = BuildReport(runMode: "Live", durationSeconds: 20, buys: 1, errors: 0, outcome: "Stopped by User");
        Assert.True(SessionReportPolicy.ShouldSave(liveShortWithTrade, new SessionReportPolicySettings(AlwaysSaveIfTradesOccurred: true)));
        Assert.False(SessionReportPolicy.ShouldSave(liveShortWithTrade, new SessionReportPolicySettings(MinDurationMinutes: 5, AlwaysSaveIfTradesOccurred: false)));

        var liveShortWithBug = BuildReport(runMode: "Live", durationSeconds: 15, buys: 0, errors: 1, outcome: "Needs Attention");
        Assert.True(SessionReportPolicy.ShouldSave(liveShortWithBug, new SessionReportPolicySettings(AlwaysSaveOnErrors: true)));
        Assert.False(SessionReportPolicy.ShouldSave(liveShortWithBug, new SessionReportPolicySettings(SaveSessionReports: false)));
    }

    [Fact]
    public async Task SessionReportStore_Saves_Filters_Prunes_And_Formats_Markdown()
    {
        var root = Path.Combine(Path.GetTempPath(), "mmopal-rpt-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new SessionReportStore(Path.Combine(root, "mmopal.db"));
            await store.InitializeAsync();

            for (var i = 1; i <= 12; i++)
            {
                var rpt = BuildReport(
                    id: $"rpt-{i:D2}",
                    accountName: i % 2 == 0 ? "Main.1234" : "Alt.5678",
                    runMode: "Live",
                    durationSeconds: 300 + i * 10,
                    buys: i,
                    errors: i == 12 ? 1 : 0,
                    outcome: i == 12 ? "Time Cap Reached" : "Completed",
                    startedAt: BaseTime.AddMinutes(i * 15));
                await store.SaveReportAsync(rpt, maxSavedReports: 10);
            }

            var allRemaining = await store.GetReportsAsync(limit: 50);
            Assert.Equal(10, allRemaining.Count);
            Assert.Equal("rpt-12", allRemaining[0].Id);
            Assert.DoesNotContain(allRemaining, r => r.Id == "rpt-01");
            Assert.DoesNotContain(allRemaining, r => r.Id == "rpt-02");

            var mainOnly = await store.GetReportsAsync("Main.1234", limit: 50);
            Assert.Equal(5, mainOnly.Count);
            Assert.All(mainOnly, r => Assert.Equal("Main.1234", r.AccountName));

            var md = SessionReportFormatter.ToMarkdown(mainOnly[0]);
            Assert.Contains("Main.1234", md, StringComparison.Ordinal);
            Assert.Contains("Time Cap Reached", md, StringComparison.Ordinal);
            Assert.Contains("## Key Metrics", md, StringComparison.Ordinal);

            await store.DeleteReportAsync("rpt-12");
            var afterDelete = await store.GetReportsAsync(limit: 50);
            Assert.DoesNotContain(afterDelete, r => r.Id == "rpt-12");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void PendingSellInventoryTracker_ConfirmedOnly_BackoffAndRemovalWork()
    {
        var tracker = new PendingSellInventoryTracker();

        // Inventory sync + Delivery pickup confirm items for listing
        tracker.SyncFromCharacterInventory(["Glob of Ectoplasm"], BaseTime);
        tracker.RecordDeliveryPickup(["Mystic Coin"], BaseTime.AddSeconds(5));
        Assert.Equal(2, tracker.Count);

        var ready = tracker.GetReadyTargets(BaseTime.AddSeconds(10), 5);
        Assert.Equal(2, ready.Count);
        Assert.Equal("Mystic Coin", ready[0]); // Most recently confirmed first

        // Listed removes item; 1st NotFoundInBags enters 5-min backoff; 2nd removes & suppresses for 15-min
        tracker.RecordSellPassResults(
            [
                new SellPassItemResult("Mystic Coin", SellPassItemOutcome.Listed),
                new SellPassItemResult("Glob of Ectoplasm", SellPassItemOutcome.NotFoundInBags)
            ],
            BaseTime.AddSeconds(20));

        Assert.Equal(1, tracker.Count);
        Assert.Empty(tracker.GetReadyTargets(BaseTime.AddMinutes(3), 5));

        var afterBackoff = tracker.GetReadyTargets(BaseTime.AddMinutes(6), 5);
        Assert.Equal(["Glob of Ectoplasm"], afterBackoff);

        // Even if character bag API still sees stack on an alt during cooldown, miss backoff is preserved
        tracker.SyncFromCharacterInventory(["Glob of Ectoplasm"], BaseTime.AddMinutes(6));
        tracker.RecordSellPassResults([new SellPassItemResult("Glob of Ectoplasm", SellPassItemOutcome.NotFoundInBags)], BaseTime.AddMinutes(6));
        Assert.Equal(0, tracker.Count);

        // Still suppressed 10m later from inventory sync...
        tracker.SyncFromCharacterInventory(["Glob of Ectoplasm"], BaseTime.AddMinutes(16));
        Assert.Equal(0, tracker.Count);

        // ...unless explicit Delivery Pickup happens on the active character!
        tracker.RecordDeliveryPickup(["Glob of Ectoplasm"], BaseTime.AddMinutes(16));
        Assert.Equal(1, tracker.Count);
    }

    [Fact]
    public async Task OfficialApi_GetUnboundInventoryItemIdsAsync_ChecksScopesAndFiltersBoundItems()
    {
        var missingScopeHandler = new ScriptedHttpHandler(req =>
        {
            var path = req.RequestUri?.AbsolutePath ?? string.Empty;
            var body = path switch
            {
                "/v2/tokeninfo" => """{"name":"NoInvKey","permissions":["account","tradingpost","wallet"]}""",
                _ => throw new InvalidOperationException($"Unexpected endpoint called without inventories scope: {path}")
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        });
        using var clientNoInv = new HttpClient(missingScopeHandler) { BaseAddress = new Uri("https://api.guildwars2.com/") };
        var noInvProvider = new Gw2OfficialDataProvider(clientNoInv, "key-no-inv");
        var (availNoInv, itemsNoInv) = await noInvProvider.GetUnboundInventoryItemIdsAsync();
        Assert.False(availNoInv);
        Assert.Empty(itemsNoInv);

        var fullHandler = new ScriptedHttpHandler(req =>
        {
            var path = req.RequestUri?.AbsolutePath ?? string.Empty;
            var body = path switch
            {
                "/v2/tokeninfo" => """{"name":"FullKey","permissions":["account","tradingpost","wallet","characters","inventories"]}""",
                "/v2/characters" =>
                    """
                    [
                      {
                        "name": "Parker One",
                        "bags": [
                          null,
                          {
                            "id": 85371,
                            "inventory": [
                              null,
                              {"id": 19721, "count": 10},
                              {"id": 46747, "count": 1, "binding": "Account"},
                              {"id": 12345, "count": 1, "binding": "Character"}
                            ]
                          }
                        ]
                      }
                    ]
                    """,
                "/v2/account/inventory" =>
                    """[null, {"id": 19976, "count": 5}, {"id": 99999, "count": 1, "binding": "Account"}]""",
                _ => throw new InvalidOperationException($"Unexpected endpoint: {path}")
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        });
        using var clientFull = new HttpClient(fullHandler) { BaseAddress = new Uri("https://api.guildwars2.com/") };
        var fullProvider = new Gw2OfficialDataProvider(clientFull, "key-full");
        var (availFull, itemIds) = await fullProvider.GetUnboundInventoryItemIdsAsync();
        Assert.True(availFull);
        Assert.Contains(19721, itemIds);
        Assert.Contains(19976, itemIds);
        Assert.DoesNotContain(46747, itemIds);
        Assert.DoesNotContain(12345, itemIds);
        Assert.DoesNotContain(99999, itemIds);
    }

    [Fact]
    public async Task WorkerRuntime_MaxSessionCap_ClosesGameSavesReportAndEmitsCapNotification()
    {
        var root = Path.Combine(Path.GetTempPath(), "mmopal-cap-flow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var dbPath = Path.Combine(root, "mmopal.db");
            var store = new CheckpointStore(dbPath);
            await store.InitializeAsync();
            var reportStore = new SessionReportStore(dbPath);
            await reportStore.InitializeAsync();

            var clock = BaseTime;
            var calls = 0;
            DateTimeOffset AdvancingNow()
            {
                calls++;
                // Allow cycle 1 to run briefly, then expire 60-minute cap
                return calls <= 2 ? BaseTime : BaseTime.AddMinutes(65);
            }

            var preparer = new CapTrackingClientPreparer();
            var fakeInput = new TestInputController();
            var fakeScreens = new TestScreenshotProvider();
            var fakeTemplates = new TestTemplateMatcher(new Dictionary<string, TemplateMatch>(StringComparer.OrdinalIgnoreCase)
            {
                ["tradingpost_tab.png"] = new(90, 20, 80, 24, 0.95m),
                ["buy_items_tab_open.png"] = new(210, 45, 70, 22, 0.95m),
                ["search.png"] = new(60, 110, 120, 24, 0.95m),
                ["item.png"] = new(220, 170, 180, 40, 0.95m),
                ["currentBuyers.png"] = new(540, 230, 100, 20, 0.95m),
                ["placeOrder.png"] = new(610, 410, 110, 30, 0.95m),
                ["logout.png"] = new(400, 415, 80, 20, 0.95m)
            });
            var fakeWindows = new TestWindowController();

            var runtime = new WorkerRuntime(
                store,
                NullLogger.Instance,
                clientPreparer: preparer,
                utcNow: AdvancingNow,
                liveInputController: fakeInput,
                liveScreenshots: fakeScreens,
                liveTemplateMatcher: fakeTemplates,
                liveWindowController: fakeWindows,
                sessionReportStore: reportStore);

            var reportEventTcs = new TaskCompletionSource<WorkerEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            runtime.EventRaised += (_, ev) =>
            {
                if (ev.Type == WorkerEventTypes.SessionReport)
                {
                    reportEventTcs.TrySetResult(ev);
                }
            };

            var payload = JsonSerializer.Serialize(new TradingReplayRequest(
                Candidates: [new ItemCandidate("Glob of Ectoplasm", Quantity: 1, BuyPriceCopper: 2_000, SellPriceCopper: 4_000)],
                Transactions: Array.Empty<Transaction>(),
                Balance: new AccountBalance(100_000),
                ObservedAt: BaseTime,
                Source: "replay",
                ClientSettings: new Gw2ClientSettings(
                    AutoPrepareOnScreenStart: false,
                    MaxSessionDurationMinutes: 60,
                    AutoCloseGameOnSessionCap: true,
                    AutoCloseAppOnSessionCap: true,
                    ShutdownPcOnSessionCap: true,
                    PcShutdownCountdownSeconds: 45,
                    UiLoadWaitMs: 25,
                    ActionSettleDelayMs: 0,
                    SwapCharacterIntervalMinutes: null),
                ArmLiveInput: true,
                EnableInventorySellPass: false,
                EnableOutbidBuyRefresh: false,
                ActiveAccountName: "CapTest.1111",
                ReportSettings: new SessionReportPolicySettings()),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));

            await runtime.HandleAsync(new WorkerCommand(WorkerCommandTypes.Start, "cap-test", BotKind.TradingPost, RunMode.Live, "balanced", payload));
            var reportEv = await reportEventTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await runtime.StopAsync(CancellationToken.None);

            Assert.Equal(1, preparer.CloseCount);
            Assert.Contains("escape", fakeInput.KeyPresses); // Graceful in-game logout attempted

            var notification = JsonSerializer.Deserialize<SessionReportNotification>(
                reportEv.Payload,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.NotNull(notification);
            Assert.True(notification.Saved);
            Assert.True(notification.CapReached);
            Assert.True(notification.AutoCloseApp);
            Assert.True(notification.ShutdownPc);
            Assert.Equal(45, notification.ShutdownCountdownSeconds);
            Assert.Equal("Time Cap Reached", notification.Report.Outcome);
            Assert.Equal("CapTest.1111", notification.Report.AccountName);

            var persisted = await reportStore.GetReportsAsync("CapTest.1111");
            Assert.Single(persisted);
            Assert.Equal("Time Cap Reached", persisted[0].Outcome);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task WorkerRuntime_DoesNotOpenSellTabForUnfilledOpenBuyOrder()
    {
        var root = Path.Combine(Path.GetTempPath(), "mmopal-no-blind-sell-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new CheckpointStore(Path.Combine(root, "mmopal.db"));
            await store.InitializeAsync();

            var fakeInput = new TestInputController();
            var fakeScreens = new TestScreenshotProvider();
            var fakeTemplates = new TestTemplateMatcher(new Dictionary<string, TemplateMatch>(StringComparer.OrdinalIgnoreCase)
            {
                ["tradingpost_tab.png"] = new(90, 20, 80, 24, 0.95m),
                ["buy_items_tab_open.png"] = new(210, 45, 70, 22, 0.95m),
                ["items_tab.png"] = new(700, 120, 60, 24, 0.95m),
                ["search.png"] = new(60, 110, 120, 24, 0.95m),
                ["item.png"] = new(220, 170, 180, 40, 0.95m),
                ["currentBuyers.png"] = new(540, 230, 100, 20, 0.95m),
                ["placeOrder.png"] = new(610, 410, 110, 30, 0.95m)
            });
            var fakeWindows = new TestWindowController();

            var runtime = new WorkerRuntime(
                store,
                NullLogger.Instance,
                liveInputController: fakeInput,
                liveScreenshots: fakeScreens,
                liveTemplateMatcher: fakeTemplates,
                liveWindowController: fakeWindows);

            var healthTcs = new TaskCompletionSource<WorkerEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            runtime.EventRaised += (_, ev) =>
            {
                if (ev.Type == WorkerEventTypes.Health)
                {
                    healthTcs.TrySetResult(ev);
                }
            };

            // Item has an active BUY order open on the TP, but nothing picked up from Delivery or confirmed in bags
            var payload = JsonSerializer.Serialize(new TradingReplayRequest(
                Candidates: [new ItemCandidate("Cleric's Draconic Boots", Quantity: 1, BuyPriceCopper: 50_000, SellPriceCopper: 90_000)],
                Transactions: [new Transaction(TransactionType.Buy, "Cleric's Draconic Boots", 50_000, 1, BaseTime, 90_000, 50_000)],
                Balance: new AccountBalance(500_000),
                ObservedAt: DateTimeOffset.UtcNow,
                Source: "replay",
                ClientSettings: new Gw2ClientSettings(UiLoadWaitMs: 25, ActionSettleDelayMs: 0, SwapCharacterIntervalMinutes: null),
                ArmLiveInput: true,
                EnableInventorySellPass: true,
                EnableOutbidBuyRefresh: false),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));

            await runtime.HandleAsync(new WorkerCommand(WorkerCommandTypes.Start, "req-no-sell", BotKind.TradingPost, RunMode.Live, "balanced", payload));
            var healthEv = await healthTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await runtime.StopAsync(CancellationToken.None);

            // Sell tab template must never be clicked & item name must never be typed for blind sell checking!
            Assert.DoesNotContain((730, 132), fakeInput.Clicks);
            Assert.DoesNotContain("Cleric's Draconic Boots", fakeInput.TextsSent);
            using var doc = JsonDocument.Parse(healthEv.Payload);
            Assert.Equal(0, doc.RootElement.GetProperty("pendingSellCount").GetInt32());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static TradingSessionReport BuildReport(
        string id = "rpt-test",
        string accountName = "Tester.1234",
        string runMode = "Live",
        double durationSeconds = 300,
        int buys = 0,
        int errors = 0,
        string outcome = "Completed",
        DateTimeOffset? startedAt = null)
    {
        var start = startedAt ?? BaseTime;
        return new TradingSessionReport(
            Id: id,
            AccountName: accountName,
            BotKind: "TradingPost",
            RunMode: runMode,
            DataSource: "screen",
            Profile: "balanced",
            StartedAt: start,
            EndedAt: start.AddSeconds(durationSeconds),
            DurationSeconds: durationSeconds,
            Outcome: outcome,
            OutcomeSummary: outcome,
            CyclesExecuted: 5,
            DecisionsEvaluated: 10,
            BuyOrdersPlaced: buys,
            SellOrdersListed: 0,
            OutbidBuysRefreshed: 0,
            DeliveryCollections: 0,
            DeliveryCoinsCollectedCopper: 0,
            CharacterSwapsCompleted: 0,
            CharacterSwapsFailed: 0,
            RecoveryAttempts: 0,
            GameRestarts: 0,
            WarningCount: 0,
            ErrorCount: errors,
            StartingWalletCopper: 100_000,
            EndingWalletCopper: 90_000,
            EstimatedBuyVolumeCopper: buys * 2000L,
            StartingOpenOrders: 0,
            EndingOpenOrders: buys,
            Trades: Array.Empty<SessionTradeEvent>(),
            Incidents: Array.Empty<SessionIncidentEvent>());
    }

    private sealed class ScriptedHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }

    private sealed class CapTrackingClientPreparer : IGw2ClientPreparer
    {
        public int CloseCount { get; private set; }

        public Task<Gw2ClientPrepareResult> PrepareAsync(
            Gw2ClientSettings settings,
            bool allowLaunch,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new Gw2ClientPrepareResult(true, false, true, "Ready"));

        public Task<Gw2ClientPrepareResult> CloseGameAsync(
            Gw2ClientSettings settings,
            CancellationToken cancellationToken = default)
        {
            CloseCount++;
            return Task.FromResult(new Gw2ClientPrepareResult(false, false, false, "Closed Guild Wars 2 cleanly."));
        }
    }

    private sealed class TestInputController : IInputController
    {
        public List<(int X, int Y)> Clicks { get; } = [];
        public List<string> KeyPresses { get; } = [];
        public List<string> TextsSent { get; } = [];

        public Task ClickAsync(int x, int y, CancellationToken cancellationToken = default)
        {
            Clicks.Add((x, y));
            return Task.CompletedTask;
        }

        public Task KeyPressAsync(string key, CancellationToken cancellationToken = default)
        {
            KeyPresses.Add(key);
            return Task.CompletedTask;
        }

        public Task SendTextAsync(string text, CancellationToken cancellationToken = default)
        {
            TextsSent.Add(text);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class TestScreenshotProvider : IScreenshotProvider
    {
        public Task<AutomationFrame> CaptureAsync(CaptureRegion region, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AutomationFrame(DateTimeOffset.UtcNow, region, Array.Empty<byte>(), "Bgra32", 0));
    }

    private sealed class TestTemplateMatcher(Dictionary<string, TemplateMatch> matches) : ITemplateMatcher
    {
        public Task<TemplateMatch?> FindAsync(AutomationFrame frame, TemplateDefinition template, CancellationToken cancellationToken = default) =>
            Task.FromResult(matches.TryGetValue(template.Name, out var match) ? match : null);
    }

    private sealed class TestWindowController : IWindowController
    {
        public Task<bool> EnsureWindowAsync(string processName, int width, int height, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }
}
