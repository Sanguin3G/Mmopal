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

/// <summary>Outbid refresh and the Delivery Box.</summary>
public sealed partial class LiveTradingExecutorTests
{
    [Fact]
    public async Task CollectDeliveryBoxAsync_ExpandsACollapsedPanelBeforeTakeAll()
    {
        // Seen live: a longer category list pushes the Delivery Box down, collapsed (header at y~681), Take All hidden.
        var input = new FakeInputController();
        var blank = new AutomationFrame(DateTimeOffset.UtcNow, CaptureRegion.FullScreen1024x768, new byte[1024 * 768 * 4], "Bgra32", 1024 * 4);
        var templates = new FakeTemplateMatcher();
        templates.DynamicMatches["take_all.png"] = () =>
            input.Clicks.Contains((139, 688)) ? new TemplateMatch(161, 704, 61, 16, 1.00m) : null;
        var locator = new FakeUiTextLocator(region => region.X == 60
            ? [new OcrTextBox("! Delivery Box", 89, 681, 100, 15), new OcrTextBox("Items: 1", 86, 702, 48, 10)]
            : Array.Empty<OcrTextBox>());
        var driver = new Gw2TradingPostUiDriver(
            new FakeScreenshotProvider { CustomFrameFactory = _ => blank },
            templates,
            input,
            new FakeWindowController(),
            templateRoot: string.Empty,
            new Gw2ClientSettings(UiLoadWaitMs: 60, ActionSettleDelayMs: 0),
            NullLogger.Instance,
            pollIntervalMs: 5,
            requireTemplateFilesOnDisk: false,
            textLocator: locator);

        Assert.True(await driver.CollectDeliveryBoxAsync());
        Assert.Equal([(139, 688), (191, 712)], input.Clicks); // expand header, then Take All
    }

    [Fact]
    public async Task CollectDeliveryBoxAsync_DoesNotClaimAPickupWhenTheBoxNeedsATraderNpc()
    {
        var input = new FakeInputController();
        var blank = new AutomationFrame(DateTimeOffset.UtcNow, CaptureRegion.FullScreen1024x768, new byte[1024 * 768 * 4], "Bgra32", 1024 * 4);
        var templates = new FakeTemplateMatcher(new Dictionary<string, TemplateMatch>(StringComparer.OrdinalIgnoreCase)
        {
            ["take_all.png"] = new(161, 704, 61, 16, 1.00m)
        });
        // OCR of the live panel when the TP was opened away from a trader.
        var locator = new FakeUiTextLocator(region => region.X == 60
            ?
            [
                new OcrTextBox("Delivery Box", 103, 475, 86, 15),
                new OcrTextBox("*rak to a Black Lion Trading", 88, 558, 208, 15),
                new OcrTextBox("Company NPC to pick up your", 87, 578, 210, 15)
            ]
            : Array.Empty<OcrTextBox>());
        var driver = new Gw2TradingPostUiDriver(
            new FakeScreenshotProvider { CustomFrameFactory = _ => blank },
            templates,
            input,
            new FakeWindowController(),
            templateRoot: string.Empty,
            new Gw2ClientSettings(UiLoadWaitMs: 60, ActionSettleDelayMs: 0),
            NullLogger.Instance,
            pollIntervalMs: 5,
            requireTemplateFilesOnDisk: false,
            textLocator: locator);

        Assert.False(await driver.CollectDeliveryBoxAsync());
        Assert.True(driver.DeliveryNeedsTraderNpc);
        Assert.DoesNotContain((191, 712), input.Clicks);
    }

    [Fact]
    public async Task CollectDeliveryBoxAsync_ReadsAnEmptyBoxAsEmptyNotLocked()
    {
        var input = new FakeInputController();
        var blank = new AutomationFrame(DateTimeOffset.UtcNow, CaptureRegion.FullScreen1024x768, new byte[1024 * 768 * 4], "Bgra32", 1024 * 4);
        var templates = new FakeTemplateMatcher(new Dictionary<string, TemplateMatch>(StringComparer.OrdinalIgnoreCase)
        {
            ["take_all.png"] = new(161, 704, 61, 16, 1.00m)
        });
        var locator = new FakeUiTextLocator(region => region.X == 60
            ?
            [
                new OcrTextBox("Delivery Box", 103, 475, 86, 15),
                new OcrTextBox("When you buy or sell on the Trading", 80, 538, 220, 15),
                new OcrTextBox("Speak to a Black Lion Trading", 88, 558, 208, 15),
                new OcrTextBox("Company NPC to pick up your", 87, 578, 210, 15)
            ]
            : Array.Empty<OcrTextBox>());
        var driver = new Gw2TradingPostUiDriver(
            new FakeScreenshotProvider { CustomFrameFactory = _ => blank },
            templates,
            input,
            new FakeWindowController(),
            templateRoot: string.Empty,
            new Gw2ClientSettings(UiLoadWaitMs: 60, ActionSettleDelayMs: 0),
            NullLogger.Instance,
            pollIntervalMs: 5,
            requireTemplateFilesOnDisk: false,
            textLocator: locator);

        Assert.False(await driver.CollectDeliveryBoxAsync());
        Assert.True(driver.DeliveryLooksEmpty);
        Assert.False(driver.DeliveryNeedsTraderNpc);
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
    public void SelectSafeOutbidCancels_passes_over_given_up_items_and_the_players_orders()
    {
        var now = new DateTimeOffset(2026, 11, 23, 3, 0, 0, TimeSpan.Zero);
        var placed = now.AddHours(-2);
        Transaction[] orders =
        [
            new(TransactionType.Buy, "Flare Dye", 900, 2, placed, null, 950),              // its search never confirms
            new(TransactionType.Buy, "Pyre Dye", 7_000, 2, placed, null, 7_100),           // ours, but the player bids on it too
            new(TransactionType.Buy, "Pyre Dye", 6_000, 5, placed, null, 7_100),           // placed by hand
            new(TransactionType.Buy, "Ebonblade", 2_000, 2, placed, null, 2_100),          // two of ours, both outbid:
            new(TransactionType.Buy, "Ebonblade", 1_900, 2, placed, null, 2_100),          // either may be cancelled
        ];

        var safe = Gw2TradingPostUiDriver.SelectSafeOutbidCancels(
            orders,
            maxCancelsPerPass: 1,
            now,
            out var ambiguous,
            out _,
            order => order.Quantity != 5,
            name => name == "Flare Dye");

        Assert.Equal("Ebonblade", Assert.Single(safe).ItemName);
        Assert.Equal(["Pyre Dye"], ambiguous);
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
    public void SelectSafeOutbidCancels_also_picks_top_bid_orders_unfilled_for_a_day()
    {
        var now = new DateTimeOffset(2026, 11, 22, 16, 0, 0, TimeSpan.Zero);
        Transaction[] orders =
        [
            // 22 Nov: 15x Valkyrie Draconic Coat at the top bid for 62 h.
            new(TransactionType.Buy, "Valkyrie Draconic Coat", 7_811, 15, now.AddHours(-62), null, 7_811),
            // At the top for most of a day: still has time to fill.
            new(TransactionType.Buy, "Carrion Noble Boots", 1_214, 2, now.AddHours(-20), null, 1_214)
        ];

        var safe = Gw2TradingPostUiDriver.SelectSafeOutbidCancels(orders, maxCancelsPerPass: 5, now, out _, out _);

        Assert.Equal(["Valkyrie Draconic Coat"], safe.Select(tx => tx.ItemName));
    }

    [Theory]
    [InlineData(6_630L, 6_630L, 6_630L)]   // the lowest ask is our own listing: join it, do not undercut ourselves
    [InlineData(6_630L, null, 6_628L)]    // no listing of ours: undercut by 2c as usual
    [InlineData(6_630L, 6_650L, 6_628L)]   // ours is above the lowest ask: undercut the other seller
    public void ChooseTypedSellPrice_joins_our_own_lowest_listing(long lowestAsk, long? ownListing, long expected)
    {
        var (target, _) = Gw2TradingPostUiDriver.ChooseTypedSellPrice(lowestAsk, lowestAsk, undercutSteps: 2, breakEvenCopper: 5_000, ownLowestListingCopper: ownListing);

        Assert.Equal(expected, target);
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
}
