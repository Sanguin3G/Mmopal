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

/// <summary>Placing buy orders: search, item checks, price choice and the visible-price guards.</summary>
public sealed partial class LiveTradingExecutorTests
{
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
                260 => [new OcrTextBox("Max. Rice o: 54: • 05:", 273, 302, 250, 20)],
                _ => Array.Empty<OcrTextBox>()
            };
        });
        var driver = new Gw2TradingPostUiDriver(
            new FakeScreenshotProvider { CustomFrameFactory = _ => blank },
            templates,
            input,
            new FakeWindowController(),
            templateRoot: string.Empty,
            new Gw2ClientSettings(UiLoadWaitMs: 80, ActionSettleDelayMs: 0, PriceEntryMode: Gw2ClientSettings.PriceEntryClickRow),
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
    public void ChooseTypedBuyPrice_uses_the_live_top_bid_and_refuses_prices_past_the_margin()
    {
        var live = Gw2TradingPostUiDriver.ChooseTypedBuyPrice(6_996, 1, 3, 2, liveTopBidCopper: 6_995);
        var apiOnly = Gw2TradingPostUiDriver.ChooseTypedBuyPrice(6_996, 1, 3, 2, liveTopBidCopper: null);
        var outbidPastMargin = Gw2TradingPostUiDriver.ChooseTypedBuyPrice(6_996, 1, 3, 2, liveTopBidCopper: 8_000);

        Assert.Equal(6_997, live.Target);
        Assert.Equal(6_997, apiOnly.Target);
        Assert.Null(outbidPastMargin.Target);
        Assert.Contains("profitable ceiling", outbidPastMargin.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EvaluateTypedBuyPrice_aborts_only_when_a_trusted_read_disagrees()
    {
        Assert.False(Gw2TradingPostUiDriver.EvaluateTypedBuyPrice(6_997, 6_997, confident: true).Abort);
        Assert.True(Gw2TradingPostUiDriver.EvaluateTypedBuyPrice(6_997, 5_799, confident: true).Abort);
        Assert.False(Gw2TradingPostUiDriver.EvaluateTypedBuyPrice(6_997, 5_799, confident: false).Abort);
        Assert.False(Gw2TradingPostUiDriver.EvaluateTypedBuyPrice(6_997, null, confident: false).Abort);
    }

    [Fact]
    public async Task PlaceBuyOrderAsync_TypeExactMode_TypesTheLiveTopBidPlusOverbid()
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
            ["arrow_up.png"] = new(596, 300, 10, 8, 1.00m),   // copper spinner, centre (601, 304)
            ["placeOrder.png"] = new(610, 410, 110, 30, 0.95m)
        });
        var locator = new FakeUiTextLocator(region => (region.X, region.Y) switch
        {
            (325, 240) => [new OcrTextBox("Pyre Dye", 393, 266, 80, 12)],
            (330, 165) => [new OcrTextBox("Pyre Dye", 347, 175, 120, 24)],
            (245, 505) => [new OcrTextBox("69. 95.", 434, 522, 60, 13), new OcrTextBox("58. 98.", 434, 550, 60, 13)],
            (260, 330) => [new OcrTextBox("Total Price: 1. 39. 94.", 273, 341, 220, 22)], // 2 x 69s 97c
            _ => Array.Empty<OcrTextBox>()
        });
        var driver = new Gw2TradingPostUiDriver(
            new FakeScreenshotProvider { CustomFrameFactory = _ => blank },
            templates,
            input,
            new FakeWindowController(),
            templateRoot: string.Empty,
            new Gw2ClientSettings(UiLoadWaitMs: 80, ActionSettleDelayMs: 0, PriceEntryMode: Gw2ClientSettings.PriceEntryTypeExact),
            NullLogger.Instance,
            pollIntervalMs: 5,
            requireTemplateFilesOnDisk: false,
            textLocator: locator);

        var result = await driver.PlaceBuyOrderAsync(
            new TradingAction(TradingActionKind.PlaceBuyOrder, "Pyre Dye", 6_996, 2, "profitable"),
            overbidMinCopper: 2,
            overbidMaxCopper: 2);

        Assert.Equal(TradingActionStatus.Completed, result.Status);
        Assert.Equal(6_997, result.PlacedUnitPriceCopper);
        Assert.Contains((425, 311), input.Clicks); // gold box
        Assert.Contains((508, 311), input.Clicks); // silver box
        Assert.Contains((580, 311), input.Clicks); // copper box
        Assert.Contains("69", input.TextsSent);
        Assert.Contains("97", input.TextsSent);
        Assert.DoesNotContain((601, 304), input.Clicks); // no spinner taps in this mode
    }

    [Fact]
    public void EvaluateVisibleBuyPrice_does_not_abort_on_an_unconfirmed_box_reading()
    {
        // Seen live: Superior Rune of the Citadel planned 27s 16c, boxes read "77s 15c" (spinner/digit misread).
        var unconfirmed = Gw2TradingPostUiDriver.EvaluateVisibleBuyPrice(2_716, 3, 7_715, confident: false);
        var confirmed = Gw2TradingPostUiDriver.EvaluateVisibleBuyPrice(2_716, 3, 7_715, confident: true);

        Assert.False(unconfirmed.Abort);
        Assert.Contains("placing anyway", unconfirmed.Message, StringComparison.Ordinal);
        Assert.True(confirmed.Abort);
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
    public async Task PlaceBuyOrderAsync_ClearsNothingInAnEmptySearchBox_AndAbortsImmediatelyWhenPaused()
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
        // The box showed its placeholder, so it was empty: nothing is cleared (Backspace is the game's Reply key).
        Assert.DoesNotContain("backspace", input.KeyPresses);
        // Letters are movement and skill keys in the world: no driver key may ever be a letter chord like Ctrl+A.
        Assert.DoesNotContain(input.KeyPresses, k => k.Contains('+'));
        Assert.DoesNotContain(input.Clicks, c => c == (439, 396)); // never clicked Place Order after pause
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
}
