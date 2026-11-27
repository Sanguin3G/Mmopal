using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Mmopal.Contracts;
using Mmopal.Gw2;
using Mmopal.Native;
using Mmopal.Worker;
using static Mmopal.Desktop.UiHelpers;

namespace Mmopal.Desktop.Pages;

/// <summary>Session controls: start, pause, resume, Stop, Emergency stop, and the client tool buttons.</summary>
public partial class SessionPage
{
    private async void LaunchSnapGw2_Click(object sender, RoutedEventArgs e)
    {
        if (!Host.SettingsPage.TryReadClientSettings(out var clientSettings)) return;
        Host.SaveClientSettings(clientSettings);
        StatusText.Text = $"Preparing Guild Wars 2 window ({clientSettings.WindowWidth}x{clientSettings.WindowHeight} at top-left)...";
        var originalContent = LaunchSnapButton.Content;
        LaunchSnapButton.IsEnabled = false;
        LaunchSnapButton.Content = "Snapping...";
        var succeeded = false;
        try
        {
            var result = await Task.Run(() => Host.ClientPreparer.PrepareAsync(clientSettings, allowLaunch: true)).ConfigureAwait(false);
            var statusMessage = result.Message;
            succeeded = result.WindowReady;

            if (result.WindowReady && Host.ClientPreparer is Gw2ClientPreparer)
            {
                var uiDriver = CreateUiDriver(clientSettings);
                var advanced = await Task.Run(() => uiDriver.TryAdvanceLauncherOrCharacterSelectAsync()).ConfigureAwait(false);
                if (advanced)
                {
                    var resnap = await Task.Run(() => Host.ClientPreparer.PrepareAsync(clientSettings, allowLaunch: false)).ConfigureAwait(false);
                    statusMessage = resnap.WindowReady
                        ? $"Advanced Launcher/Character Select and snapped Guild Wars 2 to (0, 0) ({clientSettings.WindowWidth}x{clientSettings.WindowHeight})."
                        : resnap.Message;
                }
            }

            await ShowClientToolResultAsync(LaunchSnapButton, originalContent, statusMessage, $"GW2 Client: {statusMessage}");
        }
        catch (Exception exception)
        {
            await IgnoreShutdownErrorsAsync(() => ShowClientToolResultAsync(
                LaunchSnapButton,
                originalContent,
                $"GW2 launch/snap failed: {exception.Message}",
                $"ERROR: {exception.Message}"));
        }

        await IgnoreShutdownErrorsAsync(() => FlashButtonFeedbackAsync(LaunchSnapButton, succeeded ? "✓ Snapped" : "Check status"));
    }

    private async void SwapCharacterNow_Click(object sender, RoutedEventArgs e)
    {
        if (ArmLiveInputCheckBox.IsChecked != true)
        {
            StatusText.Text = "Enable 'Arm live input' first before swapping characters.";
            AddLog("Swap char now blocked: 'Arm live input' is unchecked.");
            return;
        }

        if (!Host.SettingsPage.TryReadClientSettings(out var clientSettings)) return;
        if (clientSettings.ParkedCharacterCount <= 1)
        {
            StatusText.Text = "Set Parked Characters > 1 in Settings before swapping characters.";
            return;
        }

        Host.SaveClientSettings(clientSettings);
        StatusText.Text = "Swapping parked GW2 character (Esc -> Log Out -> target portrait)...";
        var originalContent = SwapCharacterNowButton.Content;
        SwapCharacterNowButton.IsEnabled = false;
        SwapCharacterNowButton.Content = "Swapping...";
        var succeeded = false;

        try
        {
            var prepared = await Task.Run(() => Host.ClientPreparer.PrepareAsync(clientSettings, allowLaunch: false)).ConfigureAwait(false);
            string statusMessage;
            if (!prepared.WindowReady)
            {
                statusMessage = prepared.Message;
            }
            else
            {
                var uiDriver = CreateUiDriver(clientSettings);
                (succeeded, statusMessage) = await Task.Run(() => uiDriver.RotateParkedCharacterAsync(
                    clientSettings.RandomizeCharacterSelection,
                    clientSettings.ParkedCharacterCount)).ConfigureAwait(false);
            }

            await ShowClientToolResultAsync(SwapCharacterNowButton, originalContent, statusMessage, $"Character Swap: {statusMessage}");
        }
        catch (Exception exception)
        {
            await IgnoreShutdownErrorsAsync(() => ShowClientToolResultAsync(
                SwapCharacterNowButton,
                originalContent,
                $"Character swap failed: {exception.Message}",
                $"ERROR (Character Swap): {exception.Message}"));
        }

        await IgnoreShutdownErrorsAsync(() => FlashButtonFeedbackAsync(SwapCharacterNowButton, succeeded ? "✓ Swapped" : "Failed"));
    }

    private static Gw2TradingPostUiDriver CreateUiDriver(Gw2ClientSettings clientSettings)
    {
        var templateRoot = ResolveTemplateRootDirectory();
        return new Gw2TradingPostUiDriver(
            new WindowsDesktopScreenshotProvider(),
            new FrameTemplateMatcher(),
            new Win32InputController(),
            new Win32WindowController(),
            templateRoot,
            clientSettings,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
    }

    /// <summary>
    /// Shows a client tool's outcome and gives its button back. The tool work continues off the UI thread, so this
    /// goes through the dispatcher and does nothing once the window is closing.
    /// </summary>
    private Task ShowClientToolResultAsync(Button button, object? originalContent, string status, string logLine) =>
        Dispatcher.InvokeAsync(() =>
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            StatusText.Text = status;
            AddLog(logLine);
            button.IsEnabled = true;
            button.Content = originalContent;
        }).Task;

    /// <summary>The window may close while a client tool runs; UI updates that then fail are dropped.</summary>
    private static async Task IgnoreShutdownErrorsAsync(Func<Task> uiUpdate)
    {
        try
        {
            await uiUpdate();
        }
        catch (OperationCanceledException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        var bot = Enum.Parse<BotKind>(RequiredTag(BotSelector));
        var mode = Enum.Parse<RunMode>(RequiredTag(ModeSelector));
        var profile = RequiredTag(ProfileSelector);
        var source = RequiredTag(SourceSelector);
        Host.SettingsPage.TuneBeforeSession();
        if (!Host.SettingsPage.TryReadLimits(out var limits)) return;
        if (!Host.SettingsPage.TryReadClientSettings(out var clientSettings)) return;
        if (!Host.SettingsPage.TryReadOperatorSettings(out var opSettings)) return;
        if (!TryReadGoldGoal(DateTimeOffset.Now, out var goldGoal, out var goldError))
        {
            StatusText.Text = goldError;
            AddLog(goldError);
            return;
        }

        Host.RememberGoldGoalForActiveAccount(ParseGold(KeepGoldBox.Text), ParseGold(FreeGoldBox.Text), NullIfBlank(FreeGoldByBox.Text));

        Host.SaveLimits(limits);
        Host.SaveClientSettings(clientSettings);
        Host.SaveOperatorSettings(opSettings);

        IReadOnlyList<ItemCandidate>? inlineCandidates = opSettings.UseCustomShoppingList && Host.ListsPage.CustomShoppingList.Count > 0
            ? Host.ListsPage.CustomShoppingList.ToArray()
            : null;
        IReadOnlyList<string>? inlineBlacklist = Host.ListsPage.Blacklist.Count > 0
            ? Host.ListsPage.Blacklist.ToArray()
            : null;

        long? minBalanceCopper = opSettings.GoldThresholdNoBuy is { } goldFloor
            ? checked((long)decimal.Round(goldFloor * 10_000m, 0, MidpointRounding.AwayFromZero))
            : null;

        Host.SessionStartedAtUtc = DateTimeOffset.UtcNow;
        Host.SessionExpiresAtUtc = clientSettings.MaxSessionDurationMinutes is > 0
            ? Host.SessionStartedAtUtc.Value.AddMinutes(clientSettings.MaxSessionDurationMinutes.Value)
            : null;
        Host.LastHandledCapReportId = null;
        UpdateSessionClockDisplay();

        // Re-scan during the session only when it runs on the scanner's own list, so a hand-made list is never replaced.
        MarketScanFilter? autoRescanFilter = null;
        if (Host.ScannerPage.ReadAutoRescanHours() > 0 &&
            string.Equals(Path.GetFullPath(NullIfBlank(Host.SettingsPage.CandidatesPathBox.Text) ?? "."), Path.GetFullPath(MainWindow.ScannedCandidatesPath), StringComparison.OrdinalIgnoreCase) &&
            Host.ScannerPage.TryReadScanFilter(out var currentScanFilter, out _))
        {
            autoRescanFilter = currentScanFilter;
        }

        var payload = JsonSerializer.Serialize(new TradingReplayRequest(
            Candidates: inlineCandidates,
            Blacklist: inlineBlacklist,
            Source: source,
            FixturePath: NullIfBlank(Host.SettingsPage.FixturePathBox.Text),
            RecordPath: NullIfBlank(Host.SettingsPage.RecordPathBox.Text),
            CaptureFrames: Host.SettingsPage.CaptureFramesCheckBox.IsChecked == true,
            Policy: limits.ToPolicy(),
            CandidatesPath: NullIfBlank(Host.SettingsPage.CandidatesPathBox.Text),
            BlacklistPath: NullIfBlank(Host.SettingsPage.BlacklistPathBox.Text),
            ClientSettings: clientSettings,
            MinimumBalanceCopper: minBalanceCopper,
            DefaultOrderQuantity: opSettings.DefaultOrderQuantity,
            OverbidBufferMinCopper: opSettings.OverbidBufferMinCopper,
            OverbidBufferMaxCopper: opSettings.OverbidBufferMaxCopper,
            UndercutBufferMinCopper: opSettings.UndercutBufferMinCopper,
            UndercutBufferMaxCopper: opSettings.UndercutBufferMaxCopper,
            ArmLiveInput: ArmLiveInputCheckBox.IsChecked == true,
            EnableInventorySellPass: AutoSellInventoryCheckBox.IsChecked == true,
            UsePortfolioIntelligence: opSettings.UsePortfolioIntelligence,
            ActiveAccountName: Host.ResolveActiveSqlAccountName(),
            ReportSettings: opSettings.ToSessionReportPolicy(),
            AutoRescanFilter: autoRescanFilter,
            AutoRescanIntervalMinutes: autoRescanFilter is null ? 0 : (int)Math.Round(Host.ScannerPage.ReadAutoRescanHours() * 60m),
            KeepGoldCopper: goldGoal.KeepCopper,
            FreeGoldCopper: goldGoal.FreeCopper,
            FreeGoldByUtc: goldGoal.FreeByUtc), MainWindow.PayloadJson);
        await Host.SendAsync(new WorkerCommand(WorkerCommandTypes.Start, Guid.NewGuid().ToString("N"), bot, mode, profile, payload));
    }

    /// <summary>Gold in a box ("120" or "120,5"); null when empty or not a number.</summary>
    internal static decimal? ParseGold(string? text) =>
        decimal.TryParse(text?.Trim(), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.CurrentCulture, out var gold) && gold >= 0 ? gold : null;

    /// <summary>The Gold for you row as a goal: the free-by time is the next time that clock time comes.</summary>
    internal bool TryReadGoldGoal(DateTimeOffset now, out GoldGoal goal, out string error)
    {
        goal = new GoldGoal();
        error = string.Empty;
        foreach (var (box, label) in new[] { (KeepGoldBox, "Keep"), (FreeGoldBox, "Free") })
        {
            if (!string.IsNullOrWhiteSpace(box.Text) && ParseGold(box.Text) is null)
            {
                error = $"Gold for you: '{box.Text.Trim()}' in {label} is not an amount of gold.";
                return false;
            }
        }

        DateTimeOffset? by = null;
        if (NullIfBlank(FreeGoldByBox.Text) is { } byText)
        {
            if (!TimeOnly.TryParse(byText, System.Globalization.CultureInfo.InvariantCulture, out var clock))
            {
                error = $"Gold for you: '{byText}' is not a time of day (HH:mm).";
                return false;
            }

            var today = new DateTimeOffset(now.Date + clock.ToTimeSpan(), now.Offset);
            by = (today > now ? today : today.AddDays(1)).ToUniversalTime();
        }

        static long Copper(decimal? gold) => gold is { } value ? (long)decimal.Round(value * 10_000m, 0, MidpointRounding.AwayFromZero) : 0;
        goal = new GoldGoal(Copper(ParseGold(KeepGoldBox.Text)), Copper(ParseGold(FreeGoldBox.Text)), by);
        return true;
    }

    private async void TryOfflineSample_Click(object sender, RoutedEventArgs e)
    {
        var profile = RequiredTag(ProfileSelector);
        var sample = new TradingReplayRequest(
            Candidates:
            [
                new ItemCandidate(
                    "Offline sample item",
                    Quantity: 2,
                    BuyPriceCopper: 10_000,
                    SellPriceCopper: 20_000)
            ],
            Transactions: Array.Empty<Transaction>(),
            Balance: new AccountBalance(1_000_000),
            ObservedAt: DateTimeOffset.UtcNow,
            Source: "replay");

        SourceSelector.SelectedIndex = 0;
        ModeSelector.SelectedIndex = 0;
        await Host.SendAsync(new WorkerCommand(
            WorkerCommandTypes.Start,
            Guid.NewGuid().ToString("N"),
            BotKind.TradingPost,
            RunMode.DryRun,
            profile,
            JsonSerializer.Serialize(sample, MainWindow.PayloadJson)));
    }

    /// <summary>The session selectors always have a tagged item selected.</summary>
    private static string RequiredTag(ComboBox selector) => ((ComboBoxItem)selector.SelectedItem).Tag.ToString()!;

    private async void Pause_Click(object sender, RoutedEventArgs e) => await Host.SendAsync(new WorkerCommand(WorkerCommandTypes.Pause));

    private async void Resume_Click(object sender, RoutedEventArgs e) => await Host.SendAsync(new WorkerCommand(WorkerCommandTypes.Resume));

    private async void Stop_Click(object sender, RoutedEventArgs e) => await Host.SendAsync(new WorkerCommand(WorkerCommandTypes.Stop));

    private async void EmergencyStop_Click(object sender, RoutedEventArgs e) => await Host.SendAsync(new WorkerCommand(WorkerCommandTypes.EmergencyStop));
}
