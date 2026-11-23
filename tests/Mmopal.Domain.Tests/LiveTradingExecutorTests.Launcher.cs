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

/// <summary>Launcher, Character Select, swaps, recovery and opening the Trading Post.</summary>
public sealed partial class LiveTradingExecutorTests
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

    [Fact]
    public async Task TryAdvanceLauncherOrCharacterSelectAsync_on_launch_enters_the_character_holding_the_most_stock()
    {
        // Bags were read through the API before launch: Johanne (slot #3) holds the most unsold items. The slot rule
        // ("next", 5 parked) would enter slot #5.
        var input = new FakeInputController();
        var names = new[] { "Janetta Shepard", "Big Cheetos", "Johanne Shepard", "Calanthe F Riannon", "Mesmerising Rizzo" };
        var johanne = (258 + 2 * 122, 640);
        var templates = new FakeTemplateMatcher();
        templates.DynamicMatches["charscreen_play.png"] = () =>
            input.Clicks.Contains((785, 734)) ? null : new TemplateMatch(750, 718, 71, 33, 0.97m);
        templates.DynamicMatches["ingame_1.png"] = () =>
            input.Clicks.Contains((785, 734)) ? new TemplateMatch(48, 54, 16, 16, 0.85m) : null;
        var locator = new FakeUiTextLocator(region =>
            region is { X: 40, Y: 375 } && input.Clicks.LastOrDefault(c => c.Y == 640) is { X: > 0 } portrait
                ? [new OcrTextBox(names[(portrait.X - 258) / 122], 57, 380, 220, 30)]
                : []);
        var driver = new Gw2TradingPostUiDriver(
            new FakeScreenshotProvider { CustomFrameFactory = region => new AutomationFrame(DateTimeOffset.UtcNow, region, new byte[4], "Bgra32", 4) },
            templates,
            input,
            new FakeWindowController(),
            string.Empty,
            new Gw2ClientSettings(UiLoadWaitMs: 60, ActionSettleDelayMs: 0, ParkedCharacterCount: 5, RandomizeCharacterSelection: false),
            NullLogger.Instance,
            pollIntervalMs: 5,
            requireTemplateFilesOnDisk: false,
            textLocator: locator)
        {
            PreferredCharacterNames = ["Johanne Shepard", "Mesmerising Rizzo"]
        };

        Assert.True(await driver.TryAdvanceLauncherOrCharacterSelectAsync());

        Assert.Equal(johanne, input.Clicks.Last(c => c.Y == 640)); // last portrait selected before Play
        Assert.True(input.Clicks.LastIndexOf(johanne) < input.Clicks.IndexOf((785, 734)));
        Assert.Equal("Johanne Shepard", driver.CurrentCharacterName);
    }

    [Fact]
    public async Task TryAdvanceLauncherOrCharacterSelectAsync_ignores_a_play_lookalike_outside_the_play_corner()
    {
        // Live 21-22 Nov: a red dialog button mid-screen matched charscreen_play.png on a whole-screen search;
        // clicking it (plus Enter) logged the account out to the launcher on 6 of 7 character swaps.
        var input = new FakeInputController();
        var templates = new FakeTemplateMatcher();
        templates.PredicateMatches["charscreen_play.png"] = template =>
            template.Region is { } region && region.X <= 610 && region.X + region.Width >= 670 && region.Y <= 440 && region.Y + region.Height >= 470
                ? new TemplateMatch(610, 440, 60, 32, 0.80m)
                : null;
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

        await driver.TryAdvanceLauncherOrCharacterSelectAsync();

        Assert.DoesNotContain(input.Clicks, c => c.X >= 600 && c.X <= 680 && c.Y >= 430 && c.Y <= 480);
        Assert.DoesNotContain("enter", input.KeyPresses);
    }

    [Fact]
    public async Task TryAdvanceLauncherOrCharacterSelectAsync_clicks_log_in_when_only_the_password_label_is_read()
    {
        // 22 Nov: the idle LOG IN label was misread, but the login form (its PASSWORD label) was read in place.
        var input = new FakeInputController();
        var locator = new FakeUiTextLocator(region =>
            region is { X: 10, Y: 340 } && !input.Clicks.Contains((123, 585))
                ? [new OcrTextBox("PASSWORD", 55, 464, 70, 14), new OcrTextBox("L G", 92, 579, 62, 16)]
                : []);
        var driver = new Gw2TradingPostUiDriver(
            new FakeScreenshotProvider { CustomFrameFactory = region => new AutomationFrame(DateTimeOffset.UtcNow, region, new byte[4], "Bgra32", 4) },
            new FakeTemplateMatcher(),
            input,
            new FakeWindowController(),
            string.Empty,
            new Gw2ClientSettings(UiLoadWaitMs: 200, ActionSettleDelayMs: 0),
            NullLogger.Instance,
            pollIntervalMs: 5,
            requireTemplateFilesOnDisk: false,
            textLocator: locator);

        await driver.TryAdvanceLauncherOrCharacterSelectAsync();

        Assert.Contains((123, 585), input.Clicks);
    }

    [Theory]
    [InlineData("LOG IN", true)]
    [InlineData("LOG N", true)]   // idle button, Visions of Eternity theme (22 Nov)
    [InlineData("L0G IN", true)]
    [InlineData("LOGIN", true)]
    [InlineData("LOGOUT", false)]
    [InlineData("LOGO", false)]
    [InlineData("Forgot your password?", false)]
    public void Launcher_login_label_survives_ocr_dropping_the_i(string text, bool expected) =>
        Assert.Equal(expected, Gw2TradingPostUiDriver.IsOcrLoginLabel(text));

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
        Assert.DoesNotContain("enter", input.KeyPresses); // Enter landed in the loaded world and opened chat (23 Nov)

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
    public async Task RotateParkedCharacterAsync_with_preferred_names_reads_portraits_and_enters_the_named_character()
    {
        // The character holding the most unsold stock sits in slot #3; the usual rule ("next") would pick slot #5.
        var input = new FakeInputController();
        var templates = new FakeTemplateMatcher();
        var names = new[] { "Janetta Shepard", "Big Cheetos", "Johanne Shepard", "Calanthe F Riannon", "Mesmerising Rizzo" };
        var johanneSlot = (258 + 2 * 122, 640);

        templates.DynamicMatches["logout.png"] = () =>
            input.KeyPresses.Contains("escape") && !input.Clicks.Contains((477, 443))
                ? new TemplateMatch(409, 425, 137, 37, 0.91m)
                : null;
        templates.DynamicMatches["charscreen_play.png"] = () =>
            input.Clicks.Contains((477, 443)) && !input.DoubleClicks.Contains(johanneSlot)
                ? new TemplateMatch(749, 711, 71, 33, 0.99m)
                : null;
        templates.DynamicMatches["ingame_1.png"] = () =>
            input.DoubleClicks.Contains(johanneSlot) ? new TemplateMatch(48, 54, 16, 16, 0.85m) : null;
        templates.DynamicMatches["tradingpost_tab.png"] = () =>
            input.DoubleClicks.Contains(johanneSlot) && input.KeyPresses.Contains("F")
                ? new TemplateMatch(460, 70, 40, 20, 0.95m)
                : null;
        var locator = new FakeUiTextLocator(region =>
        {
            if (region is not { X: 40, Y: 375 } || !input.Clicks.Any(c => c.Y == 640))
            {
                return [];
            }

            var lastPortrait = input.Clicks.Last(c => c.Y == 640);
            var slot = (lastPortrait.X - 258) / 122;
            // OCR drops the last letter, as live reads sometimes do.
            return [new OcrTextBox(names[slot][..^1], 57, 380, 220, 30)];
        });

        var screenshots = new FakeScreenshotProvider
        {
            CustomFrameFactory = region => new AutomationFrame(DateTimeOffset.UtcNow, region, new byte[4], "Bgra32", 4)
        };
        var driver = new Gw2TradingPostUiDriver(
            screenshots,
            templates,
            input,
            new FakeWindowController(),
            string.Empty,
            new Gw2ClientSettings(InteractKeybind: "F", UiLoadWaitMs: 60, ActionSettleDelayMs: 0),
            NullLogger.Instance,
            pollIntervalMs: 5,
            requireTemplateFilesOnDisk: false,
            textLocator: locator);

        var (succeeded, message) = await driver.RotateParkedCharacterAsync(
            randomize: false,
            parkedCharacterCount: 5,
            preferredNames: ["Johanne Shepard", "Mesmerising Rizzo"]);

        Assert.True(succeeded, message);
        Assert.Contains("slot #3", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(johanneSlot, input.DoubleClicks);
        Assert.Equal("Johanne Shepar", driver.CurrentCharacterName);
        Assert.Equal(5, driver.ParkedCharacterNames.Count); // all parked portraits read, none beyond slot 5
        Assert.DoesNotContain(input.Clicks, c => c.Y == 640 && c.X > 258 + 4 * 122);
    }

    [Theory]
    [InlineData("Mesmerising Rizzo", "Mesmerising Rizz", true)]
    [InlineData("Calanthe F Riannon", "Calanthe F. Riannon", true)]
    [InlineData("Janetta Shepard", "Johanne Shepard", false)]
    [InlineData("Big Cheetos", "", false)]
    public void CharacterStock_NamesMatch_tolerates_small_ocr_losses_only(string apiName, string ocrRead, bool expected) =>
        Assert.Equal(expected, CharacterStock.NamesMatch(apiName, ocrRead));

    [Fact]
    public void CharacterStock_ranks_characters_by_flip_items_and_picks_a_stock_swap_target()
    {
        var units = new Dictionary<string, IReadOnlyDictionary<string, long>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Mesmerising Rizzo"] = new Dictionary<string, long> { ["Twin Sisters"] = 40, ["Pyre Dye"] = 20 },
            ["Johanne Shepard"] = new Dictionary<string, long> { ["Twin Sisters"] = 24 },
            ["Big Cheetos"] = new Dictionary<string, long>(),
        };

        var ranked = CharacterStock.Rank(units, null);

        Assert.Equal(["Mesmerising Rizzo", "Johanne Shepard"], ranked.Select(entry => entry.Name));
        Assert.Equal(60, ranked[0].Items);
        Assert.Equal("Mesmerising Rizzo", CharacterStock.StockSwapTarget(ranked, "Johanne Shepard"));
        Assert.Null(CharacterStock.StockSwapTarget(ranked, "Mesmerising Rizz")); // already on it (OCR read)
        Assert.Null(CharacterStock.StockSwapTarget(ranked, null)); // current unknown: the swap could land back on it
        Assert.Null(CharacterStock.StockSwapTarget([("Big Cheetos", 4)], "Janetta Shepard")); // too little to be worth a logout
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
}
