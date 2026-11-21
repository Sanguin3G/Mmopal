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
                var templateRoot = ResolveTemplateRootDirectory();
                var uiDriver = new Gw2TradingPostUiDriver(
                    new WindowsDesktopScreenshotProvider(),
                    new FrameTemplateMatcher(),
                    new Win32InputController(),
                    new Win32WindowController(),
                    templateRoot,
                    clientSettings,
                    Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
                var advanced = await Task.Run(() => uiDriver.TryAdvanceLauncherOrCharacterSelectAsync()).ConfigureAwait(false);
                if (advanced)
                {
                    var resnap = await Task.Run(() => Host.ClientPreparer.PrepareAsync(clientSettings, allowLaunch: false)).ConfigureAwait(false);
                    statusMessage = resnap.WindowReady
                        ? $"Advanced Launcher/Character Select and snapped Guild Wars 2 to (0, 0) ({clientSettings.WindowWidth}x{clientSettings.WindowHeight})."
                        : resnap.Message;
                }
            }

            await Dispatcher.InvokeAsync(() =>
            {
                if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
                StatusText.Text = statusMessage;
                AddLog($"GW2 Client: {statusMessage}");
                LaunchSnapButton.IsEnabled = true;
                LaunchSnapButton.Content = originalContent;
            });
        }
        catch (Exception exception)
        {
            try
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
                    StatusText.Text = $"GW2 launch/snap failed: {exception.Message}";
                    AddLog($"ERROR: {exception.Message}");
                    LaunchSnapButton.IsEnabled = true;
                    LaunchSnapButton.Content = originalContent;
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }

        try
        {
            await FlashButtonFeedbackAsync(LaunchSnapButton, succeeded ? "✓ Snapped" : "Check status");
        }
        catch (OperationCanceledException)
        {
        }
        catch (InvalidOperationException)
        {
        }
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
                var templateRoot = ResolveTemplateRootDirectory();
                var uiDriver = new Gw2TradingPostUiDriver(
                    new WindowsDesktopScreenshotProvider(),
                    new FrameTemplateMatcher(),
                    new Win32InputController(),
                    new Win32WindowController(),
                    templateRoot,
                    clientSettings,
                    Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
                (succeeded, statusMessage) = await Task.Run(() => uiDriver.RotateParkedCharacterAsync(
                    clientSettings.RandomizeCharacterSelection,
                    clientSettings.ParkedCharacterCount)).ConfigureAwait(false);
            }

            await Dispatcher.InvokeAsync(() =>
            {
                if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
                StatusText.Text = statusMessage;
                AddLog($"Character Swap: {statusMessage}");
                SwapCharacterNowButton.IsEnabled = true;
                SwapCharacterNowButton.Content = originalContent;
            });
        }
        catch (Exception exception)
        {
            try
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
                    StatusText.Text = $"Character swap failed: {exception.Message}";
                    AddLog($"ERROR (Character Swap): {exception.Message}");
                    SwapCharacterNowButton.IsEnabled = true;
                    SwapCharacterNowButton.Content = originalContent;
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }

        try
        {
            await FlashButtonFeedbackAsync(SwapCharacterNowButton, succeeded ? "✓ Swapped" : "Failed");
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
        var bot = Enum.Parse<BotKind>(((ComboBoxItem)BotSelector.SelectedItem).Tag.ToString()!);
        var mode = Enum.Parse<RunMode>(((ComboBoxItem)ModeSelector.SelectedItem).Tag.ToString()!);
        var profile = ((ComboBoxItem)ProfileSelector.SelectedItem).Tag.ToString()!;
        var source = ((ComboBoxItem)SourceSelector.SelectedItem).Tag.ToString()!;
        Host.SettingsPage.TuneBeforeSession();
        if (!Host.SettingsPage.TryReadLimits(out var limits)) return;
        if (!Host.SettingsPage.TryReadClientSettings(out var clientSettings)) return;
        if (!Host.SettingsPage.TryReadOperatorSettings(out var opSettings)) return;

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
            AutoRescanIntervalMinutes: autoRescanFilter is null ? 0 : (int)Math.Round(Host.ScannerPage.ReadAutoRescanHours() * 60m)), MainWindow.PayloadJson);
        await Host.SendAsync(new WorkerCommand(WorkerCommandTypes.Start, Guid.NewGuid().ToString("N"), bot, mode, profile, payload));
    }

    private async void TryOfflineSample_Click(object sender, RoutedEventArgs e)
    {
        var profile = ((ComboBoxItem)ProfileSelector.SelectedItem).Tag.ToString()!;
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

    private async void Pause_Click(object sender, RoutedEventArgs e) => await Host.SendAsync(new WorkerCommand(WorkerCommandTypes.Pause));

    private async void Resume_Click(object sender, RoutedEventArgs e) => await Host.SendAsync(new WorkerCommand(WorkerCommandTypes.Resume));

    private async void Stop_Click(object sender, RoutedEventArgs e) => await Host.SendAsync(new WorkerCommand(WorkerCommandTypes.Stop));

    private async void EmergencyStop_Click(object sender, RoutedEventArgs e) => await Host.SendAsync(new WorkerCommand(WorkerCommandTypes.EmergencyStop));
}
