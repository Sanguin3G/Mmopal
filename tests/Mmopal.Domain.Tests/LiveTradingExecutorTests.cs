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

public sealed class LiveTradingExecutorTests
{
    [Fact]
    public async Task EnsureTradingPostOpenAsync_StopsAfterThreeBoundedAttempts_WhenTemplateNeverMatches()
    {
        var input = new FakeInputController();
        var screenshots = new FakeScreenshotProvider();
        var templates = new FakeTemplateMatcher();
        var windows = new FakeWindowController();
        var settings = new Gw2ClientSettings(
            InteractKeybind: "F",
            TradingPostKeybind: "O",
            UiLoadWaitMs: 25,
            ActionSettleDelayMs: 0);

        var driver = new Gw2TradingPostUiDriver(
            screenshots,
            templates,
            input,
            windows,
            templateRoot: string.Empty,
            settings,
            NullLogger.Instance,
            pollIntervalMs: 5,
            requireTemplateFilesOnDisk: false);

        var (ready, message) = await driver.EnsureTradingPostOpenAsync();

        Assert.False(ready);
        Assert.Contains("3 bounded attempts", message, StringComparison.OrdinalIgnoreCase);
        // F, Esc (close bank/NPC window), O (Black Lion panel), Esc, F.
        Assert.Equal(["F", "escape", "O", "escape", "F"], input.KeyPresses);
        Assert.Empty(input.Clicks);
    }

    [Fact]
    public async Task EnsureTradingPostOpenAsync_OpensOnSecondAttempt_WhenTemplateAppears()
    {
        var input = new FakeInputController();
        var screenshots = new FakeScreenshotProvider();
        var templates = new FakeTemplateMatcher();
        var windows = new FakeWindowController();

        templates.DynamicMatches["tradingpost_tab.png"] = () =>
            input.KeyPresses.Count >= 2 ? new TemplateMatch(100, 30, 80, 24, 0.95m) : null;

        var settings = new Gw2ClientSettings(
            InteractKeybind: "F",
            TradingPostKeybind: "O",
            UiLoadWaitMs: 80,
            ActionSettleDelayMs: 0);

        var driver = new Gw2TradingPostUiDriver(
            screenshots,
            templates,
            input,
            windows,
            templateRoot: string.Empty,
            settings,
            NullLogger.Instance,
            pollIntervalMs: 5,
            requireTemplateFilesOnDisk: false);

        var (ready, message) = await driver.EnsureTradingPostOpenAsync();

        Assert.True(ready);
        Assert.Contains("attempt 2", message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["F", "escape", "O"], input.KeyPresses);
    }

    [Fact]
    public async Task PlaceBuyOrderAsync_SearchesClicksAndPlacesOrder_UsingMockedTemplates()
    {
        var input = new FakeInputController();
        var screenshots = new FakeScreenshotProvider();
        var templates = new FakeTemplateMatcher(new Dictionary<string, TemplateMatch>(StringComparer.OrdinalIgnoreCase)
        {
            ["tradingpost_tab.png"] = new(90, 20, 80, 24, 0.95m),
            ["buy_items_tab.png"] = new(210, 45, 70, 22, 0.95m),
            ["search.png"] = new(60, 110, 120, 24, 0.95m),
            ["item.png"] = new(220, 170, 180, 40, 0.95m),
            ["currentBuyers.png"] = new(540, 230, 100, 20, 0.95m),
            ["quantity.png"] = new(540, 310, 60, 20, 0.95m),
            ["arrow_up.png"] = new(700, 345, 16, 16, 0.95m),
            ["placeOrder.png"] = new(610, 410, 110, 30, 0.95m)
        });
        var windows = new FakeWindowController();
        var settings = new Gw2ClientSettings(UiLoadWaitMs: 80, ActionSettleDelayMs: 0);

        var driver = new Gw2TradingPostUiDriver(
            screenshots,
            templates,
            input,
            windows,
            templateRoot: string.Empty,
            settings,
            NullLogger.Instance,
            pollIntervalMs: 5,
            requireTemplateFilesOnDisk: false);

        var action = new TradingAction(TradingActionKind.PlaceBuyOrder, "Glob of Ectoplasm", 2_500, 3, "profitable");
        var result = await driver.PlaceBuyOrderAsync(action, overbidMinCopper: 2, overbidMaxCopper: 2);

        Assert.Equal(TradingActionStatus.Completed, result.Status);
        Assert.Contains("Glob of Ectoplasm", input.TextsSent);
        Assert.Contains("3", input.TextsSent);
        Assert.Contains((708, 353), input.Clicks); // arrow_up.png center
        Assert.Contains((665, 425), input.Clicks); // placeOrder.png center
        Assert.Equal(2, input.Clicks.Count(c => c == (708, 353)));
    }

    [Fact]
    public async Task CancelOutbidBuyOrdersAsync_OnlyCancelsOutbidBuys_NeverCancelsSellListings()
    {
        var noonUtc = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
        var input = new FakeInputController();
        var screenshots = new FakeScreenshotProvider();
        var templates = new FakeTemplateMatcher(new Dictionary<string, TemplateMatch>(StringComparer.OrdinalIgnoreCase)
        {
            ["transactions.png"] = new(340, 45, 90, 24, 0.95m),
            ["transactions_current.png"] = new(40, 180, 90, 22, 0.95m),
            ["transactions_buying.png"] = new(40, 220, 90, 22, 0.95m),
            ["search.png"] = new(60, 110, 120, 24, 0.95m),
            ["transactions_cancel.png"] = new(780, 210, 60, 22, 0.95m),
            ["transactions_confirm.png"] = new(500, 260, 60, 22, 0.95m)
        });
        var windows = new FakeWindowController();
        var settings = new Gw2ClientSettings(UiLoadWaitMs: 80, ActionSettleDelayMs: 0);

        var driver = new Gw2TradingPostUiDriver(
            screenshots,
            templates,
            input,
            windows,
            templateRoot: string.Empty,
            settings,
            NullLogger.Instance,
            pollIntervalMs: 5,
            requireTemplateFilesOnDisk: false,
            utcNow: () => noonUtc);

        var placed = noonUtc.AddHours(-1);
        var transactions = new[]
        {
            new Transaction(TransactionType.Buy, "Outbid Ectoplasm", 1_000, 2, placed, null, 1_050),
            new Transaction(TransactionType.Buy, "Top Buy Ore", 1_000, 2, placed, null, 1_000),
            new Transaction(TransactionType.Sell, "Undercut Sell Sword", 5_000, 1, placed, 4_900, 4_000)
        };

        var canceled = await driver.CancelOutbidBuyOrdersAsync(transactions, maxCancelsPerPass: 5);

        Assert.Equal(1, canceled);
        Assert.Equal(["Outbid Ectoplasm"], input.TextsSent);
        Assert.Contains((810, 221), input.Clicks); // transactions_cancel.png center
        Assert.Contains((530, 271), input.Clicks); // transactions_confirm.png center
    }

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
    public async Task EnsureTradingPostOpenAsync_ClicksInteractPromptAndSwitchesFromGemStoreTab()
    {
        // Part A: Black Lion window opens on Gem Store. Scales icon measured on a live 1024x768 frame at
        // (14..53, 215..234); the sidebar search region is X=0..80, Y=195..275.
        var inputA = new FakeInputController();
        var templatesA = new FakeTemplateMatcher();
        templatesA.PredicateMatches["tradingpost_tab.png"] = tpl =>
            tpl.Region is { X: 0, Y: 195, Width: 80 }
                ? new TemplateMatch(14, 215, 39, 19, 1.00m)
                : inputA.Clicks.Contains((33, 224))
                    ? new TemplateMatch(460, 70, 40, 20, 0.90m)
                    : null;
        templatesA.PredicateMatches["transactions.png"] = _ =>
            inputA.Clicks.Contains((33, 224)) ? new TemplateMatch(867, 92, 60, 42, 0.95m) : null;

        var driverA = new Gw2TradingPostUiDriver(
            new FakeScreenshotProvider(),
            templatesA,
            inputA,
            new FakeWindowController(),
            string.Empty,
            new Gw2ClientSettings(UiLoadWaitMs: 60, ActionSettleDelayMs: 0),
            NullLogger.Instance,
            pollIntervalMs: 5,
            requireTemplateFilesOnDisk: false);

        var (readyA, msgA) = await driverA.EnsureTradingPostOpenAsync();
        Assert.True(readyA);
        Assert.Contains("Switched", msgA, StringComparison.OrdinalIgnoreCase);
        Assert.Contains((33, 224), inputA.Clicks);

        // Part B: In-world with visible "Talk [F]" prompt (interact.png)
        var inputB = new FakeInputController();
        var templatesB = new FakeTemplateMatcher();
        templatesB.DynamicMatches["interact.png"] = () =>
            inputB.Clicks.Count == 0 ? new TemplateMatch(550, 480, 32, 16, 0.91m) : null;
        templatesB.DynamicMatches["tradingpost_tab.png"] = () =>
            inputB.KeyPresses.Contains("F") ? new TemplateMatch(460, 70, 40, 20, 0.90m) : null;

        var driverB = new Gw2TradingPostUiDriver(
            new FakeScreenshotProvider(),
            templatesB,
            inputB,
            new FakeWindowController(),
            string.Empty,
            new Gw2ClientSettings(UiLoadWaitMs: 60, ActionSettleDelayMs: 0),
            NullLogger.Instance,
            pollIntervalMs: 5,
            requireTemplateFilesOnDisk: false);

        var (readyB, _) = await driverB.EnsureTradingPostOpenAsync();
        Assert.True(readyB);
        Assert.Contains((566, 488), inputB.Clicks);
        Assert.Contains("F", inputB.KeyPresses);
    }

    [Fact]
    public async Task PlaceBuyOrderAsync_SkipsSearchRowsThatOpenADifferentItem()
    {
        var input = new FakeInputController();
        var blank = new AutomationFrame(DateTimeOffset.UtcNow, CaptureRegion.FullScreen1024x768, new byte[1024 * 768 * 4], "Bgra32", 1024 * 4);
        var templates = new FakeTemplateMatcher(new Dictionary<string, TemplateMatch>(StringComparer.OrdinalIgnoreCase)
        {
            ["tradingpost_tab.png"] = new(90, 20, 80, 24, 0.95m),
            ["buy_items_tab.png"] = new(210, 45, 70, 22, 0.95m),
            ["search.png"] = new(60, 110, 120, 24, 0.95m),
            ["item.png"] = new(389, 218, 37, 18, 0.95m),        // results header, center (407, 227)
            ["currentBuyers.png"] = new(223, 452, 162, 61, 0.95m),
            ["placeOrder.png"] = new(610, 410, 110, 30, 0.95m)
        });
        // Row 1 (click at y=265) is the "with Ice Cream" cake; row 2 (y=323) is the item we want.
        var secondRowOpened = false;
        var locator = new FakeUiTextLocator(region =>
        {
            if (input.Clicks.Contains((407, 323))) secondRowOpened = true;
            return region.X switch
            {
                330 => [new OcrTextBox(secondRowOpened ? "Slice of Allspice Cake" : "Slice of Allspice Cake with Ice Cream", 347, 175, 300, 24)],
                380 => [new OcrTextBox("0: 54: • 05:", 406, 302, 150, 20)],
                _ => Array.Empty<OcrTextBox>()
            };
        });
        var driver = new Gw2TradingPostUiDriver(
            new FakeScreenshotProvider { CustomFrameFactory = _ => blank },
            templates,
            input,
            new FakeWindowController(),
            templateRoot: string.Empty,
            new Gw2ClientSettings(UiLoadWaitMs: 80, ActionSettleDelayMs: 0),
            NullLogger.Instance,
            pollIntervalMs: 5,
            requireTemplateFilesOnDisk: false,
            textLocator: locator);

        var action = new TradingAction(TradingActionKind.PlaceBuyOrder, "Slice of Allspice Cake", 5_404, 1, "profitable");
        var result = await driver.PlaceBuyOrderAsync(action, overbidMinCopper: 1, overbidMaxCopper: 1);

        Assert.Equal(TradingActionStatus.Completed, result.Status);
        Assert.Contains((407, 265), input.Clicks);
        Assert.Contains((407, 323), input.Clicks);
        Assert.Contains((665, 425), input.Clicks); // placeOrder.png center
    }

    [Fact]
    public async Task PlaceBuyOrderAsync_FallsBackToTotalPriceAndSavesACalibrationFrame()
    {
        var input = new FakeInputController();
        var blank = new AutomationFrame(DateTimeOffset.UtcNow, CaptureRegion.FullScreen1024x768, new byte[1024 * 768 * 4], "Bgra32", 1024 * 4);
        var templates = new FakeTemplateMatcher(new Dictionary<string, TemplateMatch>(StringComparer.OrdinalIgnoreCase)
        {
            ["tradingpost_tab.png"] = new(90, 20, 80, 24, 0.95m),
            ["buy_items_tab.png"] = new(210, 45, 70, 22, 0.95m),
            ["search.png"] = new(60, 110, 120, 24, 0.95m),
            ["item.png"] = new(389, 218, 37, 18, 0.95m),
            ["currentBuyers.png"] = new(223, 452, 162, 61, 0.95m),
            ["placeOrder.png"] = new(610, 410, 110, 30, 0.95m)
        });
        var totalReadable = true;
        var locator = new FakeUiTextLocator(region => (region.X, region.Y) switch
        {
            (325, 240) => [new OcrTextBox("Slice of Allspice Cake", 393, 266, 200, 12)],
            (330, 165) => [new OcrTextBox("Slice of Allspice Cake", 347, 175, 300, 24)],
            (380, 294) => Array.Empty<OcrTextBox>(),                                       // editable boxes unreadable
            (380, 333) when totalReadable => [new OcrTextBox("2. 16. 20.", 393, 341, 120, 22)], // 2 x 1g 08s 10c = 2g 16s 20c
            _ => Array.Empty<OcrTextBox>()
        });
        var diagnostics = Path.Combine(Path.GetTempPath(), "mmopal-diag-" + Guid.NewGuid().ToString("N"));
        var driver = new Gw2TradingPostUiDriver(
            new FakeScreenshotProvider { CustomFrameFactory = _ => blank },
            templates,
            input,
            new FakeWindowController(),
            templateRoot: string.Empty,
            new Gw2ClientSettings(UiLoadWaitMs: 80, ActionSettleDelayMs: 0),
            NullLogger.Instance,
            pollIntervalMs: 5,
            requireTemplateFilesOnDisk: false,
            textLocator: locator,
            diagnosticsDirectory: diagnostics);
        try
        {
            // Planned 54s 04c per unit, but the dialog total says 1g 08s 10c each: an overpay, so it must abort.
            var overpay = await driver.PlaceBuyOrderAsync(
                new TradingAction(TradingActionKind.PlaceBuyOrder, "Slice of Allspice Cake", 5_404, 2, "profitable"),
                overbidMinCopper: 1,
                overbidMaxCopper: 1);
            Assert.Equal(TradingActionStatus.Failed, overpay.Status);
            Assert.Contains("1g 08s 10c", overpay.Message, StringComparison.Ordinal);

            totalReadable = false;
            var unreadable = await driver.PlaceBuyOrderAsync(
                new TradingAction(TradingActionKind.PlaceBuyOrder, "Slice of Allspice Cake", 5_404, 2, "profitable"),
                overbidMinCopper: 1,
                overbidMaxCopper: 1);
            Assert.Equal(TradingActionStatus.Completed, unreadable.Status);
            Assert.Single(Directory.GetFiles(diagnostics, "price-unreadable-*.png"));
        }
        finally
        {
            if (Directory.Exists(diagnostics)) Directory.Delete(diagnostics, recursive: true);
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

    [Theory]
    [InlineData("Eternity", "Eternity", "Match")]
    [InlineData("Shaman's Etched Blaster of Accuracy", "Shaman 5 Etched Blaster of Accuracy", "Match")]   // OCR as seen live
    [InlineData("Superior Rune of the Scourge", "Superior Rure of the Scourge", "Match")]
    [InlineData("Slice of Allspice Cake", "Slice of Allspice Cake with Ice Cream", "Mismatch")]
    [InlineData("Strong Ring", "Strong Ring of Fire", "Mismatch")]
    [InlineData("Eternity", "Seuch :", "Unreadable")]
    [InlineData("Eternity", "", "Unreadable")]
    public void ClassifyItemTitle_tolerates_ocr_noise_but_not_other_items(string expected, string seen, string outcome)
    {
        var (result, _) = Gw2TradingPostUiDriver.ClassifyItemTitle(expected, [seen]);

        Assert.Equal(outcome, result.ToString());
    }

    [Fact]
    public async Task EnsureTradingPostOpenAsync_DismissesLoginServerNoticeAndCarriesOn()
    {
        var input = new FakeInputController();
        var templates = new FakeTemplateMatcher(new Dictionary<string, TemplateMatch>(StringComparer.OrdinalIgnoreCase)
        {
            ["tradingpost_tab.png"] = new(90, 20, 80, 24, 0.95m)
        });
        // "Unable to gain access to the log-in server ... (Code=42:...)" over the TP; OK measured at (608, 460).
        templates.DynamicMatches["ok.png"] = () =>
            input.Clicks.Contains((671, 469)) ? null : new TemplateMatch(608, 460, 127, 19, 0.98m);
        var driver = new Gw2TradingPostUiDriver(
            new FakeScreenshotProvider(),
            templates,
            input,
            new FakeWindowController(),
            templateRoot: string.Empty,
            new Gw2ClientSettings(UiLoadWaitMs: 80, ActionSettleDelayMs: 0),
            NullLogger.Instance,
            pollIntervalMs: 5,
            requireTemplateFilesOnDisk: false);

        var (ready, message) = await driver.EnsureTradingPostOpenAsync();

        Assert.True(ready, message);
        Assert.Contains((671, 469), input.Clicks);
    }

    [Theory]
    [InlineData("Slice of Allspice Cake with Ice Cream", "Slice of Allspice Cake", true)]
    [InlineData("S•undtrack Single—Visions of Eternity", "Soundtrack Single—Visions of Eternity", true)] // raw OCR seen live
    [InlineData("Superior Rure of the Scourge", "Superior Rune of the Scourge", true)]
    [InlineData("Strong Ring", "Carrion Masquerade Boots", false)]   // stale row from the previous search
    [InlineData("Eternity", "Eternity", true)]
    public void FuzzyContains_recognizes_fresh_search_rows(string row, string query, bool expected)
    {
        Assert.Equal(
            expected,
            Gw2TradingPostUiDriver.FuzzyContains(
                Gw2TradingPostUiDriver.NormalizeItemName(row),
                Gw2TradingPostUiDriver.NormalizeItemName(query)));
    }

    [Theory]
    [InlineData("3469: 69: • 67:", 34_696_967L)]     // raw OCR of the Max. Price row (Eternity)
    [InlineData("3469: e 69: • 67:", 34_696_967L)]
    [InlineData("204 : 99 : 99 :", 2_049_999L)]      // enhanced OCR (Soundtrack Single)
    [InlineData("0: 38: • 72:", 3_872L)]
    [InlineData("38: 72:", null)]                    // missing a box
    [InlineData("3469: 169: 67:", null)]             // silver out of range
    public void ParseDialogPrice_reads_gold_silver_copper_boxes(string text, long? copper)
    {
        Assert.Equal(copper, Gw2TradingPostUiDriver.ParseDialogPrice(text));
    }

    [Fact]
    public async Task PlaceBuyOrderAsync_SearchesBuyTabRegion_AndFallsBackToCogWhenSearchPlaceholderCovered()
    {
        var input = new FakeInputController();
        var templates = new FakeTemplateMatcher(new Dictionary<string, TemplateMatch>(StringComparer.OrdinalIgnoreCase)
        {
            ["tradingpost_tab.png"] = new(460, 70, 40, 20, 0.95m),
            ["buy_items_tab.png"] = new(527, 101, 77, 30, 0.95m),
            // Intentionally omit search.png / search_focused.png so cog.png -100px fallback is exercised.
            ["cog.png"] = new(280, 250, 18, 14, 0.95m),
            ["item.png"] = new(383, 288, 37, 18, 0.95m),
            ["currentBuyers.png"] = new(540, 230, 100, 20, 0.95m),
            ["quantity.png"] = new(540, 310, 60, 20, 0.95m),
            ["arrow_up.png"] = new(700, 345, 16, 16, 0.95m),
            ["placeOrder.png"] = new(610, 410, 110, 30, 0.95m)
        });

        var driver = new Gw2TradingPostUiDriver(
            new FakeScreenshotProvider(),
            templates,
            input,
            new FakeWindowController(),
            string.Empty,
            new Gw2ClientSettings(UiLoadWaitMs: 80, ActionSettleDelayMs: 0),
            NullLogger.Instance,
            pollIntervalMs: 5,
            requireTemplateFilesOnDisk: false);

        var action = new TradingAction(TradingActionKind.PlaceBuyOrder, "Deldrimor Steel Boot Casing", 12_000, 1, "profitable");
        var result = await driver.PlaceBuyOrderAsync(action, overbidMinCopper: 1, overbidMaxCopper: 1);

        Assert.Equal(TradingActionStatus.Completed, result.Status);
        Assert.Contains(templates.Queries, q => q.Name == "buy_items_tab.png" && q.Region is { X: 475, Width: 175 });
        Assert.Contains((265, 257), input.Clicks); // cog center (289, 257) - 24 X offset
        Assert.Contains("Deldrimor Steel Boot Casing", input.TextsSent);
        Assert.DoesNotContain(input.Clicks, c => c.X >= 655 && c.Y <= 240); // never clicked Sell Items tab
    }

    [Fact]
    public async Task SellInventoryPassAsync_NeverSellsUnfilteredItems_AndSearchesOnlyAllowedFlipItems()
    {
        var input = new FakeInputController();
        var templates = new FakeTemplateMatcher(new Dictionary<string, TemplateMatch>(StringComparer.OrdinalIgnoreCase)
        {
            ["items_tab.png"] = new(692, 131, 58, 63, 0.95m),
            ["cog.png"] = new(280, 250, 18, 14, 0.95m),
            ["item.png"] = new(383, 288, 37, 18, 0.95m),
            ["currentSellers.png"] = new(540, 230, 100, 20, 0.95m),
            ["ListThisItem.png"] = new(610, 410, 110, 30, 0.95m),
            ["buy_items_tab.png"] = new(527, 101, 77, 30, 0.95m)
        });

        var driver = new Gw2TradingPostUiDriver(
            new FakeScreenshotProvider(),
            templates,
            input,
            new FakeWindowController(),
            string.Empty,
            new Gw2ClientSettings(UiLoadWaitMs: 80, ActionSettleDelayMs: 0),
            NullLogger.Instance,
            pollIntervalMs: 5,
            requireTemplateFilesOnDisk: false);

        // Unfiltered and empty calls must return 0 without sending a single click or keystroke.
        Assert.Equal(0, await driver.SellInventoryPassAsync(5, 1, 2));
        Assert.Equal(0, await driver.SellInventoryPassAsync(Array.Empty<string>(), 5, 1, 2));
        Assert.Empty(input.Clicks);
        Assert.Empty(input.TextsSent);

        // Filtered call must type the explicit flip item name before selecting a row (protects Delicious Rice Ball).
        var listed = await driver.SellInventoryPassAsync(["Deldrimor Steel Boot Casing"], 5, 1, 1);

        Assert.Equal(1, listed);
        Assert.Equal(["Deldrimor Steel Boot Casing"], input.TextsSent);
        Assert.Contains((721, 162), input.Clicks); // clicked Sell Items tab
        Assert.Contains((665, 425), input.Clicks); // clicked ListThisItem.png
        Assert.Contains((565, 116), input.Clicks); // switched back to Buy Items tab afterward
    }

    [Fact]
    public async Task TryAdvanceLauncherOrCharacterSelectAsync_EntersAnotherParkedCharacterThanTheLastPlayed()
    {
        // Launch or restart: slot 1 is the character the previous run ended on (it was stuck next to a bank NPC).
        var input = new FakeInputController();
        var slotFour = (258 + 3 * 122, 640);
        var templates = new FakeTemplateMatcher();
        templates.DynamicMatches["charscreen_play.png"] = () =>
            input.Clicks.Contains((785, 734)) ? null : new TemplateMatch(750, 718, 71, 33, 0.97m);
        templates.DynamicMatches["ingame_1.png"] = () =>
            input.Clicks.Contains((785, 734)) ? new TemplateMatch(48, 54, 16, 16, 0.85m) : null;
        var driver = new Gw2TradingPostUiDriver(
            new FakeScreenshotProvider(),
            templates,
            input,
            new FakeWindowController(),
            string.Empty,
            new Gw2ClientSettings(UiLoadWaitMs: 60, ActionSettleDelayMs: 0, ParkedCharacterCount: 4, RandomizeCharacterSelection: false),
            NullLogger.Instance,
            pollIntervalMs: 5,
            requireTemplateFilesOnDisk: false);

        Assert.True(await driver.TryAdvanceLauncherOrCharacterSelectAsync());

        Assert.Contains(slotFour, input.Clicks);
        Assert.DoesNotContain((258, 640), input.Clicks);
        Assert.True(input.Clicks.IndexOf(slotFour) < input.Clicks.IndexOf((785, 734))); // portrait before Play
    }

    [Theory]
    [InlineData(false, 1, 0)]
    [InlineData(true, 1, 0)]
    [InlineData(false, 4, 3)]   // "next": least recently played parked character
    [InlineData(false, 9, 4)]   // capped at slot 5, the last portrait visible without scrolling
    [InlineData(true, 4, 2)]    // random draw stubbed below
    public void PickParkedSlotOffset_never_reenters_the_last_played_character_when_several_are_parked(bool randomize, int parked, int expected)
    {
        Assert.Equal(expected, Gw2TradingPostUiDriver.PickParkedSlotOffset(randomize, parked, (min, max) =>
        {
            Assert.Equal(1, min); // slot 1 (offset 0) is never a random candidate
            Assert.True(max <= 5);
            return 2;
        }));
    }

    [Fact]
    public async Task TryAdvanceLauncherOrCharacterSelectAsync_ClicksCharacterSelectPlay_AndDetectsThemedLauncher()
    {
        // Part A: Character Select screen
        var input = new FakeInputController();
        var templates = new FakeTemplateMatcher();
        templates.DynamicMatches["charscreen_play.png"] = () =>
            input.Clicks.Contains((785, 734)) ? null : new TemplateMatch(750, 718, 71, 33, 0.97m);
        templates.DynamicMatches["ingame_1.png"] = () =>
            input.Clicks.Contains((785, 734)) ? new TemplateMatch(48, 54, 16, 16, 0.85m) : null;

        var driver = new Gw2TradingPostUiDriver(
            new FakeScreenshotProvider(),
            templates,
            input,
            new FakeWindowController(),
            string.Empty,
            new Gw2ClientSettings(UiLoadWaitMs: 60, ActionSettleDelayMs: 0),
            NullLogger.Instance,
            pollIntervalMs: 5,
            requireTemplateFilesOnDisk: false);

        var advanced = await driver.TryAdvanceLauncherOrCharacterSelectAsync();
        Assert.True(advanced);
        Assert.Contains((785, 734), input.Clicks);
        Assert.Contains("enter", input.KeyPresses);

        // Part B: Theme-independent launcher white card + colored LOG IN / grey READY detection
        const int width = 1024;
        const int height = 768;
        const int stride = width * 4;
        var pixels = new byte[stride * height];
        for (var y = 181; y <= 718; y++)
        {
            for (var x = 20; x <= 965; x++)
            {
                var i = y * stride + x * 4;
                pixels[i] = 250; pixels[i + 1] = 250; pixels[i + 2] = 250; pixels[i + 3] = 255;
            }
        }
        // Paint orange LOG IN button around (112, 538)
        for (var y = 525; y <= 552; y++)
        {
            for (var x = 50; x <= 175; x++)
            {
                var i = y * stride + x * 4;
                pixels[i] = 74; pixels[i + 1] = 107; pixels[i + 2] = 152; pixels[i + 3] = 255;
            }
        }
        // Paint disabled dark-grey READY button around (750, 642)
        for (var y = 600; y <= 685; y++)
        {
            for (var x = 640; x <= 860; x++)
            {
                var i = y * stride + x * 4;
                pixels[i] = 64; pixels[i + 1] = 63; pixels[i + 2] = 65; pixels[i + 3] = 255;
            }
        }

        var launcherState = Gw2TradingPostUiDriver.DetectLauncherWhiteCard(
            new AutomationFrame(DateTimeOffset.UtcNow, CaptureRegion.FullScreen1024x768, pixels, "Bgra32", stride));
        Assert.NotNull(launcherState);
        Assert.True(launcherState!.LoginButtonActive);
        Assert.False(launcherState.PlayButtonActive);
        Assert.InRange(launcherState.LoginX, 95, 130);
        Assert.InRange(launcherState.LoginY, 520, 560);
    }

    [Fact]
    public async Task DetectLauncherWhiteCard_AndEnsureTradingPostOpen_HandlesEodTealThemeAndNeverTypesIntoPasswordBox()
    {
        const int width = 1024;
        const int height = 768;
        const int stride = width * 4;
        var pixels = new byte[stride * height];

        // Paint white launcher card from Y=180..712, X=20..965
        for (var y = 180; y <= 712; y++)
        {
            for (var x = 20; x <= 965; x++)
            {
                var i = y * stride + x * 4;
                pixels[i] = 250; pixels[i + 1] = 250; pixels[i + 2] = 250; pixels[i + 3] = 255;
            }
        }

        // Simulate bright Jade Mech shoulder highlights above card at (700..850, 100..175)
        for (var y = 100; y <= 175; y++)
        {
            for (var x = 700; x <= 850; x++)
            {
                var i = y * stride + x * 4;
                pixels[i] = 248; pixels[i + 1] = 252; pixels[i + 2] = 248; pixels[i + 3] = 255;
            }
        }

        // Paint EoD teal LOG IN button at (50..175, 522..548) with white center text pixels
        for (var y = 522; y <= 548; y++)
        {
            for (var x = 50; x <= 175; x++)
            {
                var i = y * stride + x * 4;
                pixels[i] = 175; pixels[i + 1] = 185; pixels[i + 2] = 25; pixels[i + 3] = 255;
            }
        }
        for (var y = 532; y <= 538; y++)
        {
            for (var x = 105; x <= 122; x++)
            {
                var i = y * stride + x * 4;
                pixels[i] = 255; pixels[i + 1] = 255; pixels[i + 2] = 250; pixels[i + 3] = 255;
            }
        }

        // Paint EoD teal READY button at (640..860, 600..685)
        for (var y = 600; y <= 685; y++)
        {
            for (var x = 640; x <= 860; x++)
            {
                var i = y * stride + x * 4;
                pixels[i] = 160; pixels[i + 1] = 145; pixels[i + 2] = 22; pixels[i + 3] = 255;
            }
        }

        var fullFrame = new AutomationFrame(DateTimeOffset.UtcNow, CaptureRegion.FullScreen1024x768, pixels, "Bgra32", stride);
        var eodState = Gw2TradingPostUiDriver.DetectLauncherWhiteCard(fullFrame);

        Assert.NotNull(eodState);
        Assert.True(eodState!.LoginButtonActive);
        Assert.False(eodState.PlayButtonActive);
        Assert.InRange(eodState.LoginX, 95, 130);
        Assert.InRange(eodState.LoginY, 520, 550);

        var input = new FakeInputController();
        var screenshots = new FakeScreenshotProvider
        {
            CustomFrameFactory = region => region.Width >= 500 && region.Height >= 500
                ? fullFrame
                : new AutomationFrame(DateTimeOffset.UtcNow, region, Array.Empty<byte>(), "Bgra32", 0)
        };
        var driver = new Gw2TradingPostUiDriver(
            screenshots,
            new FakeTemplateMatcher(),
            input,
            new FakeWindowController(),
            string.Empty,
            new Gw2ClientSettings(UiLoadWaitMs: 60, ActionSettleDelayMs: 0),
            NullLogger.Instance,
            pollIntervalMs: 5,
            requireTemplateFilesOnDisk: false);

        var (ready, message) = await driver.EnsureTradingPostOpenAsync();

        Assert.False(ready);
        Assert.Contains("Launcher", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains((eodState.LoginX, eodState.LoginY), input.Clicks);
        Assert.Empty(input.KeyPresses); // Never presses Enter, F, or O into the password field
    }

    [Fact]
    public async Task PlaceBuyOrderAsync_MovesMouseToRestPositionToClearTooltips_AndQueriesPriceSpinnerRegionOnly()
    {
        var input = new FakeInputController();
        var templates = new FakeTemplateMatcher(new Dictionary<string, TemplateMatch>(StringComparer.OrdinalIgnoreCase)
        {
            ["tradingpost_tab.png"] = new(460, 70, 40, 20, 0.95m),
            ["buy_items_tab_open.png"] = new(527, 101, 77, 30, 0.95m),
            ["search.png"] = new(60, 110, 120, 24, 0.95m),
            ["black_lion.png"] = new(10, 35, 28, 29, 0.95m),
            ["item.png"] = new(350, 290, 40, 18, 0.95m),
            ["currentBuyers.png"] = new(216, 444, 162, 61, 0.82m),
            ["quantity.png"] = new(264, 254, 70, 18, 0.75m),
            ["placeOrder.png"] = new(410, 390, 102, 14, 0.90m)
        });
        templates.PredicateMatches["arrow_up.png"] = tpl =>
            tpl.Region is { X: 545, Y: 265, Width: 80, Height: 65 }
                ? new TemplateMatch(588, 292, 10, 8, 0.99m)
                : null;

        var driver = new Gw2TradingPostUiDriver(
            new FakeScreenshotProvider(),
            templates,
            input,
            new FakeWindowController(),
            string.Empty,
            new Gw2ClientSettings(UiLoadWaitMs: 60, ActionSettleDelayMs: 0),
            NullLogger.Instance,
            pollIntervalMs: 5,
            requireTemplateFilesOnDisk: false);

        var action = new TradingAction(TradingActionKind.PlaceBuyOrder, "Snowflake Orichalcum Earring", 2, 38_837, "profitable");
        var result = await driver.PlaceBuyOrderAsync(action, overbidMinCopper: 1, overbidMaxCopper: 1);

        Assert.Equal(TradingActionStatus.Completed, result.Status);
        // Search should press Enter to immediately trigger search results
        Assert.Contains("enter", input.KeyPresses);
        // Must click Row 1 directly (CenterY + 38 = 337) and immediately select Current Buyers Row 1 without a cancelling rest click
        var itemClickIdx = input.Clicks.IndexOf((370, 337));
        Assert.True(itemClickIdx >= 0);
        Assert.Equal((297, 522), input.Clicks[itemClickIdx + 1]);
        // Must click Copper Up Arrow inside ModalPriceSpinnerRegion
        Assert.Contains((593, 296), input.Clicks);
        Assert.Contains(templates.Queries, q => q.Name == "arrow_up.png" && q.Region is { X: 545, Y: 265, Width: 80, Height: 65 });
    }

    [Fact]
    public async Task RotateParkedCharacterAsync_OpensEscapeMenuFirst_ClicksTargetPortraitAndEntersWorld()
    {
        var input = new FakeInputController();
        var templates = new FakeTemplateMatcher();
        var expectedSlot4 = (258 + 3 * 122, 640); // slot #4 portrait (624, 640)

        templates.DynamicMatches["logout.png"] = () =>
            input.KeyPresses.Contains("escape") && !input.Clicks.Contains((477, 443))
                ? new TemplateMatch(409, 425, 137, 37, 0.91m)
                : null;
        templates.DynamicMatches["charscreen_play.png"] = () =>
            input.Clicks.Contains((477, 443)) && !input.DoubleClicks.Contains(expectedSlot4)
                ? new TemplateMatch(749, 711, 71, 33, 0.99m)
                : null;
        templates.DynamicMatches["ingame_1.png"] = () =>
            input.DoubleClicks.Contains(expectedSlot4)
                ? new TemplateMatch(48, 54, 16, 16, 0.85m)
                : null;
        templates.DynamicMatches["tradingpost_tab.png"] = () =>
            input.DoubleClicks.Contains(expectedSlot4) && input.KeyPresses.Contains("F")
                ? new TemplateMatch(460, 70, 40, 20, 0.95m)
                : null;

        var driver = new Gw2TradingPostUiDriver(
            new FakeScreenshotProvider(),
            templates,
            input,
            new FakeWindowController(),
            string.Empty,
            new Gw2ClientSettings(InteractKeybind: "F", UiLoadWaitMs: 60, ActionSettleDelayMs: 0),
            NullLogger.Instance,
            pollIntervalMs: 5,
            requireTemplateFilesOnDisk: false);

        var (succeeded, message) = await driver.RotateParkedCharacterAsync(randomize: false, parkedCharacterCount: 4);

        Assert.True(succeeded);
        Assert.Contains("slot #4", message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("escape", input.KeyPresses[0]);
        Assert.DoesNotContain("f12", input.KeyPresses);
        Assert.Contains((477, 443), input.Clicks); // logout.png center
        Assert.Contains(expectedSlot4, input.Clicks); // clicked 4th character portrait
        Assert.Contains((784, 727), input.Clicks); // clicked Play button
        Assert.Contains(expectedSlot4, input.DoubleClicks); // double-clicked portrait if Play stayed up
        Assert.Contains("F", input.KeyPresses);
    }

    [Fact]
    public async Task RotateParkedCharacterAsync_RandomizedSelection_PicksOtherParkedSlotWithoutRepeatingSlotOne()
    {
        var input = new FakeInputController();
        var templates = new FakeTemplateMatcher();
        var entered = false;

        templates.DynamicMatches["logout.png"] = () =>
            input.KeyPresses.Contains("escape") && !input.Clicks.Contains((477, 443))
                ? new TemplateMatch(409, 425, 137, 37, 0.91m)
                : null;
        templates.DynamicMatches["charscreen_play.png"] = () =>
        {
            if (entered) return null;
            if (input.Clicks.Any(c => c.Y == 640 && c.X > 258) && input.Clicks.Contains((784, 727)))
            {
                entered = true;
                return null;
            }

            return input.Clicks.Contains((477, 443))
                ? new TemplateMatch(749, 711, 71, 33, 0.99m)
                : null;
        };
        templates.DynamicMatches["ingame_1.png"] = () => entered ? new TemplateMatch(48, 54, 16, 16, 0.85m) : null;
        templates.DynamicMatches["tradingpost_tab.png"] = () =>
            entered && input.KeyPresses.Contains("F")
                ? new TemplateMatch(460, 70, 40, 20, 0.95m)
                : null;

        var driver = new Gw2TradingPostUiDriver(
            new FakeScreenshotProvider(),
            templates,
            input,
            new FakeWindowController(),
            string.Empty,
            new Gw2ClientSettings(InteractKeybind: "F", UiLoadWaitMs: 60, ActionSettleDelayMs: 0),
            NullLogger.Instance,
            pollIntervalMs: 5,
            requireTemplateFilesOnDisk: false);

        var (succeeded, _) = await driver.RotateParkedCharacterAsync(randomize: true, parkedCharacterCount: 4);

        Assert.True(succeeded);
        var validTargets = new[] { 258 + 122, 258 + 2 * 122, 258 + 3 * 122 };
        Assert.Contains(input.Clicks, c => c.Y == 640 && validTargets.Contains(c.X));
        Assert.DoesNotContain(input.Clicks, c => c == (258, 640)); // never repeats Slot #1
    }

    [Fact]
    public async Task TryAdvanceLauncherOrCharacterSelectAsync_PollsAutologinUntilPlayAppears()
    {
        const int width = 1024;
        const int height = 768;
        const int stride = width * 4;

        var autologinWaitPixels = new byte[stride * height];
        for (var y = 180; y <= 712; y++)
        {
            for (var x = 20; x <= 965; x++)
            {
                var i = y * stride + x * 4;
                autologinWaitPixels[i] = 250; autologinWaitPixels[i + 1] = 250; autologinWaitPixels[i + 2] = 250; autologinWaitPixels[i + 3] = 255;
            }
        }
        // Disabled grey READY button (no LOG IN box because -autologin is authenticating)
        for (var y = 600; y <= 685; y++)
        {
            for (var x = 640; x <= 860; x++)
            {
                var i = y * stride + x * 4;
                autologinWaitPixels[i] = 64; autologinWaitPixels[i + 1] = 63; autologinWaitPixels[i + 2] = 65; autologinWaitPixels[i + 3] = 255;
            }
        }

        var playReadyPixels = (byte[])autologinWaitPixels.Clone();
        // Active orange PLAY button around (757, 636)
        for (var y = 600; y <= 685; y++)
        {
            for (var x = 640; x <= 860; x++)
            {
                var i = y * stride + x * 4;
                playReadyPixels[i] = 40; playReadyPixels[i + 1] = 110; playReadyPixels[i + 2] = 210; playReadyPixels[i + 3] = 255;
            }
        }

        var polls = 0;
        var input = new FakeInputController();
        var templates = new FakeTemplateMatcher();
        templates.DynamicMatches["charscreen_play.png"] = () =>
            input.Clicks.Any(c => c.X > 700 && c.Y > 600)
                ? new TemplateMatch(749, 711, 71, 33, 0.99m)
                : null;
        templates.DynamicMatches["ingame_1.png"] = () =>
            input.Clicks.Contains((784, 727))
                ? new TemplateMatch(48, 54, 16, 16, 0.85m)
                : null;

        var screenshots = new FakeScreenshotProvider
        {
            CustomFrameFactory = region =>
            {
                if (region.Width < 500 || region.Height < 500)
                {
                    return new AutomationFrame(DateTimeOffset.UtcNow, region, Array.Empty<byte>(), "Bgra32", 0);
                }

                polls++;
                if (input.Clicks.Any(c => c.X > 700 && c.Y > 600))
                {
                    return new AutomationFrame(DateTimeOffset.UtcNow, region, Array.Empty<byte>(), "Bgra32", 0);
                }

                var buf = polls >= 2 ? playReadyPixels : autologinWaitPixels;
                return new AutomationFrame(DateTimeOffset.UtcNow, region, buf, "Bgra32", stride);
            }
        };

        var driver = new Gw2TradingPostUiDriver(
            screenshots,
            templates,
            input,
            new FakeWindowController(),
            string.Empty,
            new Gw2ClientSettings(UiLoadWaitMs: 300, ActionSettleDelayMs: 0),
            NullLogger.Instance,
            pollIntervalMs: 5,
            requireTemplateFilesOnDisk: false);

        var advanced = await driver.TryAdvanceLauncherOrCharacterSelectAsync();

        Assert.True(advanced);
        Assert.Contains(input.Clicks, c => c.X >= 700 && c.Y >= 600 && c.Y < 700); // clicked PLAY button
        Assert.Contains((784, 727), input.Clicks); // entered via Character Select Play
    }

    [Fact]
    public async Task DetectLauncherWhiteCard_HandlesPeachNewsViewPlayCentroid_AndScopesLoginTemplateRegion()
    {
        const int width = 1024;
        const int height = 768;
        const int stride = width * 4;
        var pixels = new byte[stride * height];

        // Paint VoE peach/cream LATEST NEWS card across (20..965, 180..712)
        for (var y = 180; y <= 712; y++)
        {
            for (var x = 20; x <= 965; x++)
            {
                var i = y * stride + x * 4;
                pixels[i] = 223; pixels[i + 1] = 239; pixels[i + 2] = 255; pixels[i + 3] = 255;
            }
        }

        // Paint orange PLAY button shifted right/down inside live launcher band at (790..870, 650..695)
        for (var y = 650; y <= 695; y++)
        {
            for (var x = 790; x <= 870; x++)
            {
                var i = y * stride + x * 4;
                pixels[i] = 43; pixels[i + 1] = 88; pixels[i + 2] = 155; pixels[i + 3] = 255;
            }
        }

        var fullFrame = new AutomationFrame(DateTimeOffset.UtcNow, CaptureRegion.FullScreen1024x768, pixels, "Bgra32", stride);
        var state = Gw2TradingPostUiDriver.DetectLauncherWhiteCard(fullFrame);

        Assert.NotNull(state);
        Assert.False(state!.LoginButtonActive);
        Assert.True(state.PlayButtonActive);
        Assert.InRange(state.PlayX, 780, 880);
        Assert.InRange(state.PlayY, 645, 700);

        var templates = new FakeTemplateMatcher();
        templates.PredicateMatches["login_launcher.png"] = tpl =>
            tpl.Region is { X: 20, Y: 440, Width: 280, Height: 220 }
                ? null
                : new TemplateMatch(470, 695, 42, 20, 0.88m); // false match on download bar must be ignored

        var input = new FakeInputController();
        var driver = new Gw2TradingPostUiDriver(
            new FakeScreenshotProvider { CustomFrameFactory = _ => fullFrame },
            templates,
            input,
            new FakeWindowController(),
            string.Empty,
            new Gw2ClientSettings(UiLoadWaitMs: 60, ActionSettleDelayMs: 0),
            NullLogger.Instance,
            pollIntervalMs: 5,
            requireTemplateFilesOnDisk: false);

        _ = await driver.TryAdvanceLauncherOrCharacterSelectAsync();

        Assert.Contains(input.Clicks, c => c.X >= 780 && c.Y >= 645); // clicked real PLAY centroid, never (491, 705)
        Assert.DoesNotContain(input.Clicks, c => c.X < 600);
    }

    [Fact]
    public void DetectLauncherWhiteCard_HandlesDarkBottomLws4AndPofThemesWithWarmCardSurround()
    {
        const int width = 1024;
        const int height = 768;
        const int stride = width * 4;
        var pixels = new byte[stride * height];

        // Paint top white card (Y=180..444, X=10..980) and warm sunset LWS4 card area (Y=445..580, X=10..480)
        // with dark purple mountain artwork below Y=580 so raw white cardBottom stops at 444.
        for (var y = 180; y <= 444; y++)
        {
            for (var x = 10; x <= 980; x++)
            {
                var i = y * stride + x * 4;
                pixels[i] = 248; pixels[i + 1] = 248; pixels[i + 2] = 252; pixels[i + 3] = 255;
            }
        }

        for (var y = 445; y <= 580; y++)
        {
            for (var x = 10; x <= 480; x++)
            {
                var i = y * stride + x * 4;
                // Warm sunset background from LWS4 screenshot: R=228, G=188, B=168
                pixels[i] = 168; pixels[i + 1] = 188; pixels[i + 2] = 228; pixels[i + 3] = 255;
            }
        }

        // Paint orange LOG IN button at fixed HTML offset (X=48..172, Y=519..552)
        for (var y = 519; y <= 552; y++)
        {
            for (var x = 48; x <= 172; x++)
            {
                var i = y * stride + x * 4;
                pixels[i] = 30; pixels[i + 1] = 75; pixels[i + 2] = 155; pixels[i + 3] = 255;
            }
        }

        var frame = new AutomationFrame(DateTimeOffset.UtcNow, CaptureRegion.FullScreen1024x768, pixels, "Bgra32", stride);
        var state = Gw2TradingPostUiDriver.DetectLauncherWhiteCard(frame);

        Assert.NotNull(state);
        Assert.True(state!.LoginButtonActive);
        Assert.False(state.PlayButtonActive);
        Assert.InRange(state.LoginX, 95, 130);
        Assert.InRange(state.LoginY, 520, 550);
    }

    [Fact]
    public async Task PlaceBuyOrderAsync_DoubleClearsSearchBox_AndAbortsImmediatelyWhenPaused()
    {
        var paused = false;
        var input = new FakeInputController();
        var templates = new FakeTemplateMatcher(new Dictionary<string, TemplateMatch>(StringComparer.OrdinalIgnoreCase)
        {
            ["tradingpost_tab.png"] = new(460, 70, 40, 20, 0.95m),
            ["buy_items_tab_open.png"] = new(527, 101, 77, 30, 0.95m),
            ["search.png"] = new(60, 110, 120, 24, 0.95m),
            ["item.png"] = new(350, 290, 40, 18, 0.95m),
            ["currentBuyers.png"] = new(216, 444, 162, 61, 0.85m),
            ["arrow_up.png"] = new(588, 292, 10, 8, 0.99m),
            ["placeOrder.png"] = new(388, 389, 102, 14, 0.99m)
        });

        // Trigger pause as soon as Current Buyers Row 1 is clicked so Place Order must NOT be clicked
        templates.DynamicMatches["arrow_up.png"] = () =>
        {
            paused = true;
            return new TemplateMatch(588, 292, 10, 8, 0.99m);
        };

        var driver = new Gw2TradingPostUiDriver(
            new FakeScreenshotProvider(),
            templates,
            input,
            new FakeWindowController(),
            string.Empty,
            new Gw2ClientSettings(UiLoadWaitMs: 80, ActionSettleDelayMs: 0),
            NullLogger.Instance,
            pollIntervalMs: 5,
            requireTemplateFilesOnDisk: false,
            isPaused: () => paused);

        var action = new TradingAction(TradingActionKind.PlaceBuyOrder, "Cleric's Draconic Helm", 1, 50_000, "profitable");
        var result = await driver.PlaceBuyOrderAsync(action, overbidMinCopper: 1, overbidMaxCopper: 1);

        Assert.Equal(TradingActionStatus.Skipped, result.Status);
        Assert.Equal(2, input.KeyPresses.Count(k => k == "ctrl+a"));
        Assert.Equal(2, input.KeyPresses.Count(k => k == "backspace"));
        Assert.DoesNotContain(input.Clicks, c => c == (439, 396)); // never clicked Place Order after pause
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
    public async Task TryAdvanceLauncherOrCharacterSelectAsync_ClicksJanthirLogInAndNeverClicksBrightBlueReady()
    {
        const int width = 1024;
        const int height = 768;
        const int stride = width * 4;
        var pixels = new byte[stride * height];

        // Paint pale icy-blue Janthir Wilds card background (R=218, G=230, B=245 — darker than 230!)
        for (var y = 180; y <= 710; y++)
        {
            for (var x = 20; x <= 965; x++)
            {
                var i = y * stride + x * 4;
                pixels[i] = 245; pixels[i + 1] = 230; pixels[i + 2] = 218; pixels[i + 3] = 255;
            }
        }

        // Paint bright cyan LOG IN at (50..175, 525..552) and bright blue READY at (640..860, 600..665)
        for (var y = 525; y <= 552; y++)
        {
            for (var x = 50; x <= 175; x++)
            {
                var i = y * stride + x * 4;
                pixels[i] = 185; pixels[i + 1] = 155; pixels[i + 2] = 45; pixels[i + 3] = 255;
            }
        }

        for (var y = 600; y <= 665; y++)
        {
            for (var x = 640; x <= 860; x++)
            {
                var i = y * stride + x * 4;
                pixels[i] = 220; pixels[i + 1] = 140; pixels[i + 2] = 20; pixels[i + 3] = 255;
            }
        }

        var frame = new AutomationFrame(DateTimeOffset.UtcNow, CaptureRegion.FullScreen1024x768, pixels, "Bgra32", stride);
        var input = new FakeInputController();
        var locator = new FakeUiTextLocator(region =>
        {
            if (region.X < 400)
            {
                return [new OcrTextBox("LOG IN", 72, 535, 76, 20)];
            }

            return [new OcrTextBox("READY", 710, 620, 88, 22)];
        });

        var driver = new Gw2TradingPostUiDriver(
            new FakeScreenshotProvider { CustomFrameFactory = _ => frame },
            new FakeTemplateMatcher(),
            input,
            new FakeWindowController(),
            string.Empty,
            new Gw2ClientSettings(UiLoadWaitMs: 60, ActionSettleDelayMs: 0, LaunchPlatform: Gw2ClientSettings.PlatformArenaNet),
            NullLogger.Instance,
            pollIntervalMs: 5,
            requireTemplateFilesOnDisk: false,
            textLocator: locator);

        var advanced = await driver.TryAdvanceLauncherOrCharacterSelectAsync();

        Assert.True(advanced);
        Assert.Contains(input.Clicks, c => c.X is >= 70 and <= 150 && c.Y is >= 530 and <= 560);
        Assert.DoesNotContain(input.Clicks, c => c.X > 600); // Never falsely clicked bright-blue READY!
        Assert.Empty(input.KeyPresses);
    }

    [Fact]
    public async Task TryAdvanceLauncherOrCharacterSelectAsync_DoesNotClickLogInOnceLauncherMovedToNews()
    {
        const int width = 1024;
        const int height = 768;
        const int stride = width * 4;
        var pixels = new byte[stride * height];
        for (var y = 180; y <= 710; y++)
        {
            for (var x = 20; x <= 965; x++)
            {
                var i = y * stride + x * 4;
                pixels[i] = 250; pixels[i + 1] = 250; pixels[i + 2] = 250; pixels[i + 3] = 255;
            }
        }

        var frame = new AutomationFrame(DateTimeOffset.UtcNow, CaptureRegion.FullScreen1024x768, pixels, "Bgra32", stride);
        var input = new FakeInputController();
        var loginReads = 0;
        // LOG IN is visible on the first frame only; by the time the driver is about to click,
        // login has gone through and the launcher shows the news page instead.
        var locator = new FakeUiTextLocator(region =>
            region.X < 400 && Interlocked.Increment(ref loginReads) == 1
                ? [new OcrTextBox("LOG IN", 72, 535, 76, 20)]
                : Array.Empty<OcrTextBox>());

        var driver = new Gw2TradingPostUiDriver(
            new FakeScreenshotProvider { CustomFrameFactory = _ => frame },
            new FakeTemplateMatcher(),
            input,
            new FakeWindowController(),
            string.Empty,
            new Gw2ClientSettings(UiLoadWaitMs: 60, ActionSettleDelayMs: 0, LaunchPlatform: Gw2ClientSettings.PlatformArenaNet),
            NullLogger.Instance,
            pollIntervalMs: 5,
            requireTemplateFilesOnDisk: false,
            textLocator: locator);

        await driver.TryAdvanceLauncherOrCharacterSelectAsync();

        Assert.True(loginReads >= 2);
        Assert.DoesNotContain(input.Clicks, c => c.X < 400);
    }

    [Theory]
    [InlineData(3_872L, 3_874L, false)]   // normal +2c overbid
    [InlineData(3_872L, 3_990L, false)]   // small drift inside 3% tolerance
    [InlineData(3_872L, 5_400L, true)]    // misclick onto a seller row: clearly overpaying
    [InlineData(3_872L, 38_720L, false)]  // 10x off: OCR misread, do not block
    [InlineData(3_872L, 3_800L, false)]   // top bid dropped a little: still a fillable bid
    [InlineData(3_674L, 3_105L, true)]    // seen live: 15% under the top bid, can never fill
    [InlineData(3_872L, null, false)]     // unreadable: place as before
    public void EvaluateVisibleBuyPrice_aborts_only_on_plausible_overpay(long planned, long? visible, bool abort)
    {
        var check = Gw2TradingPostUiDriver.EvaluateVisibleBuyPrice(planned, overbidMaxCopper: 5, visible);

        Assert.Equal(abort, check.Abort);
        Assert.False(string.IsNullOrWhiteSpace(check.Message));
    }

    [Fact]
    public void SelectSafeOutbidCancels_skips_orders_whose_search_would_show_several_of_our_buys()
    {
        var now = DateTimeOffset.UtcNow;
        var placed = now.AddHours(-1);
        Transaction[] orders =
        [
            new(TransactionType.Buy, "Strong Ring", 3_874, 2, placed, null, 3_880),                 // outbid, but "Strong Ring of Fire" also matches
            new(TransactionType.Buy, "Strong Ring of Fire", 9_000, 1, placed, null, 8_000),         // not outbid
            new(TransactionType.Buy, "Mini Green Raptor Hatchling", 16_437, 2, placed, null, 16_500), // outbid, duplicate below
            new(TransactionType.Buy, "Mini Green Raptor Hatchling", 16_500, 1, placed, null, 16_500), // the order that is on top
            new(TransactionType.Buy, "Carrion Masquerade Boots", 3_674, 2, placed, null, 3_700),    // outbid, unique: safe
            new(TransactionType.Sell, "Strong Ring", 9_999, 1, placed, 9_000, null)
        ];

        var safe = Gw2TradingPostUiDriver.SelectSafeOutbidCancels(orders, maxCancelsPerPass: 5, now, out var ambiguous, out _);

        Assert.Equal(["Carrion Masquerade Boots"], safe.Select(tx => tx.ItemName));
        Assert.Equal(["Strong Ring", "Mini Green Raptor Hatchling"], ambiguous);
    }

    [Fact]
    public void SelectSafeOutbidCancels_keeps_young_orders_so_they_can_fill()
    {
        var now = new DateTimeOffset(2026, 11, 20, 6, 50, 0, TimeSpan.Zero);
        Transaction[] orders =
        [
            // Seen live: undercut by 1c two minutes after placing. Keep it.
            new(TransactionType.Buy, "Carrion Exalted Mantle", 5_035, 2, now.AddMinutes(-2), null, 5_036),
            // Still outbid after 25 minutes: refresh it.
            new(TransactionType.Buy, "Superior Rune of the Scourge", 9_270, 2, now.AddMinutes(-25), null, 9_277)
        ];

        var safe = Gw2TradingPostUiDriver.SelectSafeOutbidCancels(orders, maxCancelsPerPass: 5, now, out _, out var keptYoung);

        Assert.Equal(["Superior Rune of the Scourge"], safe.Select(tx => tx.ItemName));
        Assert.Equal(1, keptYoung);
    }

    [Fact]
    public async Task CancelOutbidBuyOrdersAsync_KeepsEveryOrderDuringDailyResetWindow()
    {
        var resetUtc = new DateTimeOffset(2026, 11, 21, 0, 30, 0, TimeSpan.Zero);
        var input = new FakeInputController();
        var driver = new Gw2TradingPostUiDriver(
            new FakeScreenshotProvider(),
            new FakeTemplateMatcher(),
            input,
            new FakeWindowController(),
            templateRoot: string.Empty,
            new Gw2ClientSettings(UiLoadWaitMs: 80, ActionSettleDelayMs: 0),
            NullLogger.Instance,
            pollIntervalMs: 5,
            requireTemplateFilesOnDisk: false,
            utcNow: () => resetUtc);

        var canceled = await driver.CancelOutbidBuyOrdersAsync(
            [new Transaction(TransactionType.Buy, "Outbid Ectoplasm", 1_000, 2, resetUtc.AddHours(-2), null, 1_050)],
            maxCancelsPerPass: 5);

        Assert.Equal(0, canceled);
        Assert.Empty(input.Clicks);
    }

    [Fact]
    public async Task TryAdvanceLauncherOrCharacterSelectAsync_SteamPlatformSkipsLogInClickAndEntersPlay()
    {
        const int width = 1024;
        const int height = 768;
        const int stride = width * 4;
        var pixels = new byte[stride * height];
        for (var y = 180; y <= 710; y++)
        {
            for (var x = 20; x <= 965; x++)
            {
                var i = y * stride + x * 4;
                pixels[i] = 250; pixels[i + 1] = 250; pixels[i + 2] = 250; pixels[i + 3] = 255;
            }
        }

        var frame = new AutomationFrame(DateTimeOffset.UtcNow, CaptureRegion.FullScreen1024x768, pixels, "Bgra32", stride);
        var input = new FakeInputController();
        var templates = new FakeTemplateMatcher();
        templates.DynamicMatches["play.png"] = () =>
            input.Clicks.Any(c => c.X >= 700 && c.Y >= 600) ? null : new TemplateMatch(740, 620, 120, 50, 0.92m);
        templates.DynamicMatches["charscreen_play.png"] = () =>
            input.Clicks.Contains((800, 645)) && !input.Clicks.Contains((785, 734))
                ? new TemplateMatch(750, 718, 71, 33, 0.96m)
                : null;
        templates.DynamicMatches["ingame_1.png"] = () =>
            input.Clicks.Contains((785, 734)) ? new TemplateMatch(48, 54, 16, 16, 0.90m) : null;

        var locator = new FakeUiTextLocator(region =>
        {
            if (region.X < 400) return [new OcrTextBox("LOG IN", 72, 535, 76, 20)];
            return Array.Empty<OcrTextBox>();
        });

        var driver = new Gw2TradingPostUiDriver(
            new FakeScreenshotProvider
            {
                CustomFrameFactory = _ => input.Clicks.Any(c => c.X >= 700)
                    ? new AutomationFrame(DateTimeOffset.UtcNow, CaptureRegion.FullScreen1024x768, Array.Empty<byte>(), "Bgra32", 0)
                    : frame
            },
            templates,
            input,
            new FakeWindowController(),
            string.Empty,
            new Gw2ClientSettings(
                ExecutablePath: Gw2ClientSettings.SteamLaunchUri,
                LaunchPlatform: Gw2ClientSettings.PlatformSteam,
                UiLoadWaitMs: 60,
                ActionSettleDelayMs: 0),
            NullLogger.Instance,
            pollIntervalMs: 5,
            requireTemplateFilesOnDisk: false,
            textLocator: locator);

        var advanced = await driver.TryAdvanceLauncherOrCharacterSelectAsync();

        Assert.True(advanced);
        Assert.DoesNotContain(input.Clicks, c => c.X < 400 && c.Y is > 480 and < 600); // Steam mode never clicks LOG IN (portrait row at y=640 is fine)
        Assert.Contains((800, 645), input.Clicks);
        Assert.Contains((785, 734), input.Clicks);
    }

    [Fact]
    public async Task TryRecoverTradingSessionAsync_DismissesOkDialogEntersCharacterSelectViaOcrAndOpensTp()
    {
        var input = new FakeInputController();
        var templates = new FakeTemplateMatcher();
        // No charscreen_play.png template! Tests OCR fallback for Play + Character Slots context.
        templates.DynamicMatches["ok.png"] = () =>
            !input.Clicks.Contains((512, 430)) ? new TemplateMatch(480, 415, 64, 30, 0.98m) : null;
        templates.DynamicMatches["ingame_1.png"] = () =>
            input.Clicks.Contains((785, 730)) ? new TemplateMatch(48, 54, 16, 16, 0.90m) : null;
        templates.DynamicMatches["tradingpost_tab.png"] = () =>
            input.Clicks.Contains((785, 730)) && input.KeyPresses.Contains("F")
                ? new TemplateMatch(460, 70, 40, 20, 0.95m)
                : null;

        var locator = new FakeUiTextLocator(region =>
        {
            if (input.Clicks.Contains((785, 730)))
            {
                return Array.Empty<OcrTextBox>();
            }

            if (region is { X: 0, Y: 680, Width: 1024 })
            {
                return
                [
                    new OcrTextBox("Log Out", 20, 718, 68, 22),
                    new OcrTextBox("10 of 13 Character Slots in Use", 400, 725, 220, 20)
                ];
            }

            if (region is { X: 680, Y: 680, Width: 220 })
            {
                return [new OcrTextBox("Play", 760, 718, 50, 24)];
            }

            return Array.Empty<OcrTextBox>();
        });

        var nonEmptyPixels = new byte[1024 * 768 * 4];
        var fullFrame = new AutomationFrame(DateTimeOffset.UtcNow, CaptureRegion.FullScreen1024x768, nonEmptyPixels, "Bgra32", 1024 * 4);

        var driver = new Gw2TradingPostUiDriver(
            new FakeScreenshotProvider { CustomFrameFactory = _ => fullFrame },
            templates,
            input,
            new FakeWindowController(),
            string.Empty,
            new Gw2ClientSettings(InteractKeybind: "F", UiLoadWaitMs: 60, ActionSettleDelayMs: 0),
            NullLogger.Instance,
            pollIntervalMs: 5,
            requireTemplateFilesOnDisk: false,
            textLocator: locator);

        var (ready, message) = await driver.TryRecoverTradingSessionAsync();

        Assert.True(ready);
        Assert.Contains("Trading Post opened", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains((512, 430), input.Clicks); // Dismissed ok.png
        Assert.Contains((785, 730), input.Clicks); // Clicked OCR "Play" button
        Assert.Contains("F", input.KeyPresses);
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

    private sealed class FakeUiTextLocator(Func<CaptureRegion, IReadOnlyList<OcrTextBox>> responder) : IUiTextLocator
    {
        public Task<IReadOnlyList<OcrTextBox>> LocateLinesAsync(
            AutomationFrame frame,
            CaptureRegion region,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(responder(region));
        }
    }

    private sealed class RestartTrackingPreparer : IGw2ClientPreparer
    {
        public int RestartCount { get; private set; }

        public Task<Gw2ClientPrepareResult> PrepareAsync(
            Gw2ClientSettings settings,
            bool allowLaunch,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new Gw2ClientPrepareResult(true, false, true, "Ready"));

        public Task<Gw2ClientPrepareResult> RestartAsync(
            Gw2ClientSettings settings,
            CancellationToken cancellationToken = default)
        {
            RestartCount++;
            return Task.FromResult(new Gw2ClientPrepareResult(true, true, false, "Restarted"));
        }
    }

    private sealed class FakeInputController : IInputController
    {
        public List<(int X, int Y)> Clicks { get; } = [];
        public List<(int X, int Y)> DoubleClicks { get; } = [];
        public List<string> KeyPresses { get; } = [];
        public List<string> TextsSent { get; } = [];

        public Task ClickAsync(int x, int y, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Clicks.Add((x, y));
            return Task.CompletedTask;
        }

        public Task DoubleClickAsync(int x, int y, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DoubleClicks.Add((x, y));
            Clicks.Add((x, y));
            return Task.CompletedTask;
        }

        public Task KeyPressAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            KeyPresses.Add(key);
            return Task.CompletedTask;
        }

        public Task SendTextAsync(string text, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TextsSent.Add(text);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeScreenshotProvider : IScreenshotProvider
    {
        public Func<CaptureRegion, AutomationFrame>? CustomFrameFactory { get; init; }

        public Task<AutomationFrame> CaptureAsync(CaptureRegion region, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (CustomFrameFactory is not null)
            {
                return Task.FromResult(CustomFrameFactory(region));
            }

            return Task.FromResult(new AutomationFrame(DateTimeOffset.UtcNow, region, Array.Empty<byte>(), "Bgra32", 0));
        }
    }

    private sealed class FakeTemplateMatcher : ITemplateMatcher
    {
        private readonly Dictionary<string, TemplateMatch> _matches;

        public FakeTemplateMatcher(Dictionary<string, TemplateMatch>? matches = null)
        {
            _matches = matches ?? new Dictionary<string, TemplateMatch>(StringComparer.OrdinalIgnoreCase);
        }

        public List<(string Name, CaptureRegion? Region)> Queries { get; } = [];
        public Dictionary<string, Func<TemplateMatch?>> DynamicMatches { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Func<TemplateDefinition, TemplateMatch?>> PredicateMatches { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<TemplateMatch?> FindAsync(AutomationFrame frame, TemplateDefinition template, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Queries.Add((template.Name, template.Region));
            if (PredicateMatches.TryGetValue(template.Name, out var predicate))
            {
                return Task.FromResult(predicate(template));
            }

            if (DynamicMatches.TryGetValue(template.Name, out var factory))
            {
                return Task.FromResult(factory());
            }

            return Task.FromResult(_matches.TryGetValue(template.Name, out var match) ? match : null);
        }
    }

    private sealed class FakeWindowController : IWindowController
    {
        public Task<bool> EnsureWindowAsync(string processName, int width, int height, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(true);
        }
    }
}
