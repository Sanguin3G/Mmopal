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

/// <summary>Reading and parsing prices, totals and item titles.</summary>
public sealed partial class LiveTradingExecutorTests
{
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
            (260, 290) => Array.Empty<OcrTextBox>(),                                                   // editable boxes unreadable
            (260, 330) when totalReadable => [new OcrTextBox("Total Price: 2. 16. 20.", 273, 341, 220, 22)], // 2 x 1g 08s 10c
            _ => Array.Empty<OcrTextBox>()
        });
        var diagnostics = Path.Combine(Path.GetTempPath(), "mmopal-diag-" + Guid.NewGuid().ToString("N"));
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

    [Theory]
    [InlineData("69. 95.", 6_995L, 6_995L)]            // live Pyre Dye frame: the top row the click method missed
    [InlineData("1. 13. 36.", 11_336L, 11_336L)]       // live frame, gold shown
    [InlineData("23. 67.", 2_366L, 2_367L)]
    [InlineData("2,721.69.", 27_216_900L, 27_216_900L)] // 2,721g 69s: copper 0 omitted
    [InlineData("79.0. 4.", 790_004L, 790_004L)]
    [InlineData("61 —O. 66•", 610_066L, 610_066L)]      // "O" read for 0
    [InlineData("5. 00.", 6_995L, null)]               // neither 5g nor 5s is near 69s 95c: misread
    [InlineData("neo. 3.", 6_995L, null)]
    public void ParseBuyerRowPrice_resolves_omitted_units_against_the_api_hint(string text, long hint, long? expected)
    {
        Assert.Equal(expected, Gw2TradingPostUiDriver.ParseBuyerRowPrice(text, hint));
    }

    [Theory]
    [InlineData("Total Price: 14e 6e", 1_406L)]        // live List This Item dialog, enhanced read
    [InlineData("Total Price: 144 6.", null)]         // raw read of the same line: coin icon read as "4"; rejected
    [InlineData("Total Price: 10• 21 e", 1_021L)]     // live Sell Instantly dialog
    public void ParseTotalPrice_handles_the_sell_dialog_total(string text, long? expected)
    {
        Assert.Equal(expected, Gw2TradingPostUiDriver.ParseTotalPrice(text));
    }

    [Theory]
    [InlineData("Total Price: 1 18 e", 10_018L, true)]        // 1g 00s 18c with "00" unread (15 Slot Rugged Leather Pack, 34 aborts)
    [InlineData("Total Price: 6e 44e", 30_022L, true)]        // 2 x 3g 00s 22c = 6g 00s 44c
    [InlineData("Total Price: 1 78", 8_903L, true)]           // 2 x 89s 03c = 1g 78s 06c, "06" unread
    [InlineData("Total Price: 1", 5_000L, true)]              // 2 x 50s = 1g 00s 00c
    [InlineData("Total Price: 56 74", 12_837L, false)]        // 2 x 1g 28s 37c: gold missing is never accepted
    [InlineData("Total Price: 1 19", 10_018L, false)]         // different copper
    [InlineData("Total Price: 1 00 18", 10_018L, true)]       // full read
    public void TotalMatchesWithUnreadZeroGroups_accepts_only_missing_zero_groups(string text, long unit, bool expected)
    {
        Assert.Equal(expected, Gw2TradingPostUiDriver.TotalMatchesWithUnreadZeroGroups(text, unit));
    }

    [Theory]
    [InlineData("Total Price: 56• 56•", "Total Price: 2• 56 e", 12_828L, true)]    // 2 x 1g 28s 28c: gold in one read, copper in the other
    [InlineData("Total Price: 56• 56•", "Total Price: 56 56", 12_828L, false)]     // no read shows the gold: the box really missed it
    [InlineData("Total Price: 81 e", "Total Price: 88€ 814", 8_881L, true)]        // coin icon read as a trailing 4
    [InlineData("Total Price: 27 • 87 •", "Total Price: 274. 87 e", 8_196L, false)] // Berserker's Amulet: total ignores the typed price
    public void TotalMatchesAcrossReads_combines_the_raw_and_enhanced_reads(string first, string second, long unit, bool expected)
    {
        Assert.Equal(expected, Gw2TradingPostUiDriver.TotalMatchesAcrossReads([first, second], unit));
    }

    [Theory]
    [InlineData(true, 15_160L, 15_156L, true)]
    [InlineData(true, null, 15_156L, false)]     // Genesis, 22 Nov: title and live price both unreadable (it was "Gaze")
    [InlineData(true, 778_504L, 15_156L, false)] // Gaze's real price
    [InlineData(false, 15_160L, 15_156L, false)] // the search list never showed the name: stale rows
    public void An_unread_title_needs_the_search_list_and_the_live_price_as_proof(bool listed, long? live, long api, bool expected) =>
        Assert.Equal(expected, Gw2TradingPostUiDriver.UnreadTitleIsProven(listed, live, api));

    [Theory]
    [InlineData(30_314L, 15_157L, 2L, true)]
    [InlineData(15_157L, 15_157L, 2L, true)]
    [InlineData(840_063L, 40_003L, 2L, false)]   // 21x ordered instead of 2 (20 Nov)
    [InlineData(1_901_294L, 215_043L, 2L, false)]
    public void Total_must_be_the_typed_price_times_at_most_the_ordered_quantity(long total, long unit, long quantity, bool expected) =>
        Assert.Equal(expected, Gw2TradingPostUiDriver.IsWholeMultiple(total, unit, quantity));

    [Theory]
    [InlineData("Eternity", "Eternity", "Match")]
    [InlineData("Shaman's Etched Blaster of Accuracy", "Shaman 5 Etched Blaster of Accuracy", "Match")]   // OCR as seen live
    [InlineData("Superior Rune of the Scourge", "Superior Rure of the Scourge", "Match")]
    [InlineData("Slice of Allspice Cake", "Slice of Allspice Cake with Ice Cream", "Mismatch")]
    [InlineData("Strong Ring", "Strong Ring of Fire", "Mismatch")]
    // The dialog cuts long titles (22 Nov); the variants differ before the cut, and a short cut proves nothing.
    [InlineData("Soldier's Benthic Waterbreather of the Afflicted", "Soldier's Benthic Waterbreather of the", "Match")]
    [InlineData("Soldier's Benthic Waterbreather of the Afflicted", "Soldier's Benthic Aquabreather of the", "Mismatch")]
    [InlineData("Soldier's Benthic Waterbreather of the Afflicted", "Soldier's Benthic Hydrobreather of the", "Mismatch")]
    [InlineData("Superior Rune of the Scourge Extra", "Superior Rune of the Scour", "Mismatch")]
    // 22 Nov: the (Red) infusion passed for (Purple) at 85% alike and 190g went into the wrong order.
    [InlineData("Polysaturating Reverberating Infusion (Purple)", "Polysaturating Reverberating Infusion (Red)", "Mismatch")]
    [InlineData("Polysaturating Reverberating Infusion (Purple)", "Polysaturating Reverberating Infusion", "Mismatch")]
    [InlineData("Polysaturating Reverberating Infusion (Purple)", "Polysaturating Reverberating Infusion (Purp1e)", "Match")]
    [InlineData("+8 Agony Infusion", "+9 Agony Infusion", "Mismatch")]
    [InlineData("15 Slot Linen Bag", "18 Slot Linen Bag", "Mismatch")]
    [InlineData("+8 Agony Infusion", "+8 Agony lnfusion", "Match")]
    [InlineData("Superior Rune of the Renegade", "Superior Rune of the Revenant", "Mismatch")]
    [InlineData("Eternity", "Seuch :", "Unreadable")]
    [InlineData("Eternity", "", "Unreadable")]
    public void ClassifyItemTitle_tolerates_ocr_noise_but_not_other_items(string expected, string seen, string outcome)
    {
        var (result, _) = Gw2TradingPostUiDriver.ClassifyItemTitle(expected, [seen]);

        Assert.Equal(outcome, result.ToString());
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
    [InlineData("3469: 169: 67:", 34_691_667L)]     // spinner read as an extra digit after silver
    [InlineData("Max. Rice o: 23: • 67:", 2_367L)]   // live frame: gold 0 read as "o"
    [InlineData("B4ax. Rice o: 582 e 99:", 5_899L)]  // live frame: label garbled, spinner read as "2"
    [InlineData("Max. Rice 1: e 13: • 31 : e", 11_331L)]
    public void ParseDialogPrice_reads_gold_silver_copper_boxes(string text, long? copper)
    {
        Assert.Equal(copper, Gw2TradingPostUiDriver.ParseDialogPrice(text));
    }

    [Theory]
    [InlineData("Total Price: 47. 34 e", 4_734L)]          // live: gold omitted when it is 0
    [InlineData("Total Price: I e 17 • 98.", 11_798L)]     // live: 1 read as "I"
    [InlineData("Total Price: 2e 26• 62.", 22_662L)]
    [InlineData("Total Price: 47 e 34e", 4_734L)]
    [InlineData("Total Price: 3,469. 69. 67.", 34_696_967L)]
    [InlineData("Total Price:", null)]
    [InlineData("Total Price: 1 2 3 4", null)]
    public void ParseTotalPrice_reads_the_total_line_seen_live(string text, long? copper)
    {
        Assert.Equal(copper, Gw2TradingPostUiDriver.ParseTotalPrice(text));
    }
}
