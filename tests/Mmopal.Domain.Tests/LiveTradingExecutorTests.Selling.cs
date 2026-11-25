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

/// <summary>Listing bought items: typed sell price, break-even floor, filtered bag sell pass.</summary>
public sealed partial class LiveTradingExecutorTests
{
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
    public void ChooseTypedSellPrice_undercuts_the_lowest_ask_but_never_below_break_even()
    {
        var normal = Gw2TradingPostUiDriver.ChooseTypedSellPrice(1_406, liveLowestAskCopper: 1_406, undercutSteps: 1, breakEvenCopper: 1_200);
        var apiOnly = Gw2TradingPostUiDriver.ChooseTypedSellPrice(1_406, liveLowestAskCopper: null, undercutSteps: 2, breakEvenCopper: null);
        var held = Gw2TradingPostUiDriver.ChooseTypedSellPrice(1_406, liveLowestAskCopper: 1_406, undercutSteps: 1, breakEvenCopper: 1_500);

        Assert.Equal(1_405, normal.Target);
        Assert.Equal(1_404, apiOnly.Target);
        Assert.Null(held.Target);
        Assert.Contains("break-even", held.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1_000L, 1_177L)]   // 1,177c - 59c listing - 118c exchange = 1,000c
    [InlineData(5_404L, 6_358L)]
    [InlineData(1L, 3L)]           // 1c minimum fees
    public void BreakEvenListingPrice_covers_cost_after_fees(long cost, long expected)
    {
        var price = SellPricing.BreakEvenListingPrice(cost);
        var engine = new ProfitabilityEngine();

        Assert.Equal(expected, price);
        Assert.True(engine.Evaluate(cost, price, new ProfitabilityPolicy()).ProfitCopper >= 0);
        Assert.True(engine.Evaluate(cost, price - 1, new ProfitabilityPolicy()).ProfitCopper < 0);
    }

    [Fact]
    public void Held_sell_targets_wait_thirty_minutes()
    {
        var tracker = new PendingSellInventoryTracker();
        var now = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
        tracker.RecordNewBuyFill(["Mist-Infused Maintenance Oil"], now);

        tracker.RecordSellPassResults([new SellPassItemResult("Mist-Infused Maintenance Oil", SellPassItemOutcome.Held, "below break-even")], now);

        Assert.Empty(tracker.GetReadyTargets(now.AddMinutes(20), 3));
        Assert.Single(tracker.GetReadyTargets(now.AddMinutes(31), 3));
    }

    [Fact]
    public async Task SellInventoryPass_TypeExactMode_ListsAtTheLiveLowestAskMinusUndercut()
    {
        var input = new FakeInputController();
        var blank = new AutomationFrame(DateTimeOffset.UtcNow, CaptureRegion.FullScreen1024x768, new byte[1024 * 768 * 4], "Bgra32", 1024 * 4);
        var templates = new FakeTemplateMatcher(new Dictionary<string, TemplateMatch>(StringComparer.OrdinalIgnoreCase)
        {
            ["items_tab.png"] = new(710, 75, 58, 63, 1.00m),
            ["search.png"] = new(74, 191, 44, 16, 1.00m),
            ["item.png"] = new(389, 182, 37, 18, 0.95m),
            ["currentSellers.png"] = new(535, 466, 123, 24, 1.00m),
            ["arrow_up.png"] = new(596, 300, 10, 8, 1.00m),
            ["ListThisItem.png"] = new(392, 396, 110, 18, 0.95m)
        });
        var locator = new FakeUiTextLocator(region => (region.X, region.Y) switch
        {
            (325, 240) => [new OcrTextBox("Mist-Infused Maintenance Oil", 393, 290, 150, 12)],
            (330, 165) => [new OcrTextBox("Mist-Infused Maintenance Oil", 347, 175, 300, 24)],
            (530, 505) => [new OcrTextBox("14. 6.", 727, 522, 50, 13), new OcrTextBox("14. 7.", 727, 550, 50, 13)],
            (260, 330) => [new OcrTextBox("Total Price: 14e 5e", 273, 341, 220, 22)],   // 14s 05c after typing
            _ => Array.Empty<OcrTextBox>()
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
        var pricing = new Dictionary<string, SellPricing>(StringComparer.OrdinalIgnoreCase)
        {
            ["Mist-Infused Maintenance Oil"] = new(ApiLowestAskCopper: 1_406, BreakEvenCopper: 1_200)
        };

        var results = await driver.SellInventoryPassDetailedAsync(["Mist-Infused Maintenance Oil"], 3, 1, 1, CancellationToken.None, pricing);

        var listed = Assert.Single(results);
        Assert.Equal(SellPassItemOutcome.Listed, listed.Outcome);
        Assert.Contains("14s 05c (typed)", listed.Details, StringComparison.Ordinal);
        Assert.Contains("14", input.TextsSent);
        Assert.Contains("5", input.TextsSent);
        Assert.Contains((447, 405), input.Clicks); // List This Item
        Assert.DoesNotContain(input.Clicks, c => c == (601, 315)); // never taps the down spinner in typed mode
    }

    [Fact]
    public async Task A_buyer_at_the_typed_price_makes_it_list_at_the_lowest_ask_instead()
    {
        // 24 Nov, Arms of the People: bid 40s 96c, ask 40s 97c. At 40s 96c the dialog offers Sell Instantly, not List.
        var input = new FakeInputController();
        var blank = new AutomationFrame(DateTimeOffset.UtcNow, CaptureRegion.FullScreen1024x768, new byte[1024 * 768 * 4], "Bgra32", 1024 * 4);
        bool AtTheAsk() => input.TextsSent.Contains("97");
        var templates = new FakeTemplateMatcher(new Dictionary<string, TemplateMatch>(StringComparer.OrdinalIgnoreCase)
        {
            ["items_tab.png"] = new(710, 75, 58, 63, 1.00m),
            ["search.png"] = new(74, 191, 44, 16, 1.00m),
            ["item.png"] = new(389, 182, 37, 18, 0.95m),
            ["currentSellers.png"] = new(535, 466, 123, 24, 1.00m),
            ["arrow_up.png"] = new(596, 300, 10, 8, 1.00m)
        });
        templates.DynamicMatches["ListThisItem.png"] = () => AtTheAsk() ? new TemplateMatch(392, 396, 110, 18, 0.95m) : null;
        var locator = new FakeUiTextLocator(region => (region.X, region.Y) switch
        {
            (325, 240) => [new OcrTextBox("Arms of the People", 393, 290, 150, 12)],
            (330, 165) => [new OcrTextBox("Arms of the People", 347, 175, 300, 24)],
            (530, 505) => [new OcrTextBox("40. 97.", 727, 522, 50, 13)],
            (260, 330) => [new OcrTextBox(AtTheAsk() ? "Total Price: 40e 97e" : "Total Price: 40e 96e", 273, 341, 220, 22)],
            _ => Array.Empty<OcrTextBox>()
        });
        var driver = new Gw2TradingPostUiDriver(
            new FakeScreenshotProvider { CustomFrameFactory = _ => blank }, templates, input, new FakeWindowController(), string.Empty,
            new Gw2ClientSettings(UiLoadWaitMs: 60, ActionSettleDelayMs: 0), NullLogger.Instance, pollIntervalMs: 5, requireTemplateFilesOnDisk: false, textLocator: locator);
        var pricing = new Dictionary<string, SellPricing>(StringComparer.OrdinalIgnoreCase) { ["Arms of the People"] = new(4_097, 3_202) };

        var listed = Assert.Single(await driver.SellInventoryPassDetailedAsync(["Arms of the People"], 3, 1, 1, CancellationToken.None, pricing));

        Assert.Equal(SellPassItemOutcome.Listed, listed.Outcome);
        Assert.Equal(4_097, listed.UnitCopper);
        Assert.Contains((447, 405), input.Clicks);
    }

    [Theory]
    [InlineData(true)]   // known to the pass, but the API gave no price
    [InlineData(false)]
    public async Task Nothing_is_listed_without_a_price_to_check_it_against(bool hasEntry)
    {
        // 24 Nov: with the API down, four items were listed by the arrows, with no break-even check.
        var input = new FakeInputController();
        var driver = new Gw2TradingPostUiDriver(
            new FakeScreenshotProvider(),
            new FakeTemplateMatcher(new Dictionary<string, TemplateMatch>(StringComparer.OrdinalIgnoreCase)
            {
                ["items_tab.png"] = new(710, 75, 58, 63, 1.00m),
                ["search.png"] = new(74, 191, 44, 16, 1.00m),
                ["item.png"] = new(389, 182, 37, 18, 0.95m),
                ["currentSellers.png"] = new(535, 466, 123, 24, 1.00m),
                ["ListThisItem.png"] = new(392, 396, 110, 18, 0.95m)
            }),
            input,
            new FakeWindowController(),
            string.Empty,
            new Gw2ClientSettings(UiLoadWaitMs: 60, ActionSettleDelayMs: 0),
            NullLogger.Instance,
            pollIntervalMs: 5,
            requireTemplateFilesOnDisk: false);
        var pricing = new Dictionary<string, SellPricing>(StringComparer.OrdinalIgnoreCase);
        if (hasEntry)
        {
            pricing["Carbonized Mithrillium Ingot"] = new(ApiLowestAskCopper: null, BreakEvenCopper: 15_740);
        }

        var results = await driver.SellInventoryPassDetailedAsync(["Carbonized Mithrillium Ingot"], 3, 1, 1, CancellationToken.None, pricing);

        Assert.Equal(SellPassItemOutcome.SkippedError, Assert.Single(results).Outcome);
        Assert.Empty(input.TextsSent);
        Assert.DoesNotContain((447, 405), input.Clicks); // List This Item
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
}
