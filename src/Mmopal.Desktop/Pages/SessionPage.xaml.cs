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

/// <summary>Session: start and steer a trading session, watch its status, and read the activity log.</summary>
public partial class SessionPage : UserControl
{
    private readonly List<(DateTime Timestamp, string Human, string Raw)> _logEntries = new();
    private MainWindow? _host;

    public SessionPage()
    {
        InitializeComponent();
        ShowRawLogCheckBox.Checked += (_, _) => RebuildLogList();
        ShowRawLogCheckBox.Unchecked += (_, _) => RebuildLogList();
    }

    private MainWindow Host => _host ?? throw new InvalidOperationException("The page is not attached to the main window.");

    private bool Loading => _host is null || _host.IsLoadingUi;

    internal void Attach(MainWindow host) => _host = host;

    private void DockToggle_Click(object sender, RoutedEventArgs e) => Host.ToggleSideDock();

    private void OpenGuide_Click(object sender, RoutedEventArgs e) => Host.NavGuide.IsChecked = true;

    private void DismissReadinessBanner_Click(object sender, RoutedEventArgs e)
    {
        Host.ReadinessBannerDismissed = true;
        ReadinessBanner.Visibility = Visibility.Collapsed;
    }

    private void ModeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Loading) return;

        // Live trading acts on the real game, so it always reads the real game screen and arms input; any other mode
        // sends no input, so it disarms.
        var isLive = IsSelectedTag(ModeSelector, nameof(RunMode.Live));
        if (isLive && SourceSelector is not null && !IsSelectedTag(SourceSelector, "screen"))
        {
            SelectByTag(SourceSelector, "screen");
        }

        if (ArmLiveInputCheckBox is not null)
        {
            ArmLiveInputCheckBox.IsChecked = isLive;
        }

        UpdateSafeModePill();
    }

    private void SourceSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Loading || ModeSelector is null) return;

        // Replay data is a recorded sample; arming live clicks against it makes no sense, so fall back to Dry run.
        if (IsSelectedTag(SourceSelector, "replay") && IsSelectedTag(ModeSelector, nameof(RunMode.Live)))
        {
            SelectByTag(ModeSelector, nameof(RunMode.DryRun));
        }
    }

    private void ProfileSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Loading) return;
        UpdateSessionPaceSummary();
    }

    internal void UpdateSessionPaceSummary()
    {
        if (SessionPaceSummaryText is null || ProfileSelector is null)
        {
            return;
        }

        var profileTag = (ProfileSelector.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "balanced";
        var paceText = TradingPaceDescriptions.Describe(profileTag);
        var platform = Host.SettingsPage.GetSelectedLaunchPlatform();
        var acct = Host.ApiKeyVault.ActiveEntry?.DisplayLabel;
        SessionPaceSummaryText.Text = string.IsNullOrWhiteSpace(acct)
            ? $"{paceText}  •  Platform: {platform}"
            : $"{paceText}  •  Platform: {platform} ({acct})";
        // The pace description is a tooltip, not a line of text on the page.
        ProfileSelector.ToolTip = SessionPaceSummaryText.Text;
        if (PaceSafeItem is not null)
        {
            PaceSafeItem.ToolTip = TradingPaceDescriptions.Describe("safe");
            PaceBalancedItem.ToolTip = TradingPaceDescriptions.Describe("balanced");
            PaceFastItem.ToolTip = TradingPaceDescriptions.Describe("fast");
        }
    }

    private void AutoSellInventory_Changed(object sender, RoutedEventArgs e)
    {
        if (Loading) return;
        Host.SettingsPage.SaveAllSettingsSilently(); // remembered across restarts; it used to reset to off on every launch
    }

    private void ArmLiveInput_Changed(object sender, RoutedEventArgs e)
    {
        if (Loading) return;
        UpdateSafeModePill();
    }

    internal void UpdateSafeModePill()
    {
        if (ModeSelector is null || ArmLiveInputCheckBox is null ||
            SafeModePillBorder is null || SafeModePillIcon is null || SafeModePillText is null)
        {
            return;
        }

        var selectedMode = (ModeSelector.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        var isLiveMode = string.Equals(selectedMode, nameof(RunMode.Live), StringComparison.OrdinalIgnoreCase);
        var isArmed = ArmLiveInputCheckBox.IsChecked == true;

        if (isLiveMode && isArmed)
        {
            SafeModePillText.Text = "Live · Armed";
            SafeModePillBorder.ToolTip = "Live input is armed: the session sends real clicks and keystrokes to Guild Wars 2.";
            SafeModePillIcon.Text = "\uE7BA";
            var warnBrush = ResolveBrush("WarningBrush", Brushes.DarkOrange);
            SafeModePillIcon.Foreground = warnBrush;
            SafeModePillBorder.BorderBrush = warnBrush;
        }
        else if (isLiveMode)
        {
            SafeModePillText.Text = "Live · Disarmed";
            SafeModePillBorder.ToolTip = "Live mode is selected but input is not armed. Tick 'Arm live input' to let the session click.";
            SafeModePillIcon.Text = "\uE72E";
            var warnBrush = ResolveBrush("WarningBrush", Brushes.DarkOrange);
            SafeModePillIcon.Foreground = warnBrush;
            SafeModePillBorder.BorderBrush = ResolveBrush("BorderBrush", Brushes.Gray);
        }
        else
        {
            SafeModePillText.Text = "Safe · No input";
            SafeModePillBorder.ToolTip = "Dry run / Observe: no clicks or keystrokes are sent to the game.";
            SafeModePillIcon.Text = "\uE72E";
            SafeModePillIcon.Foreground = ResolveBrush("SuccessBrush", Brushes.SeaGreen);
            SafeModePillBorder.BorderBrush = ResolveBrush("BorderBrush", Brushes.Gray);
        }
    }

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

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        _logEntries.Clear();
        LogList.Items.Clear();
        UpdateLogCount();
    }

    private enum LaunchStep { Waiting, Running, Done, Skipped, Failed }

    /// <summary>One startup step on the Session page: a mark (○ … ✓ – !) plus a few words.</summary>
    private void SetLaunchStep(TextBlock target, LaunchStep step, string text)
    {
        var (mark, brushKey) = step switch
        {
            LaunchStep.Running => ("…", "AccentBrush"),
            LaunchStep.Done => ("✓", "SuccessBrush"),
            LaunchStep.Skipped => ("–", "TextMutedBrush"),
            LaunchStep.Failed => ("!", "WarningBrush"),
            _ => ("○", "TextMutedBrush")
        };
        target.Text = $"{mark} {text}";
        target.Foreground = ResolveBrush(brushKey, Brushes.Gray);
    }

    /// <summary>
    /// On launch: sync the account (Income &amp; Portfolio), Auto-Tune, then scan the market so the Scanner is never
    /// empty. A scanned session list older than the Auto re-scan interval is replaced with the fresh scan; a hand-made
    /// list is never touched. Progress shows in the Startup strip on the Session page.
    /// </summary>
    internal async Task RunLaunchAutomationAsync()
    {
        await RunLaunchAutomationCoreAsync();
        // A clean startup only needs a glance: fold the strip away after a minute. Anything failed (!) stays visible.
        if (LaunchStepsStrip.Visibility == Visibility.Visible &&
            !new[] { LaunchSyncText.Text, LaunchTuneText.Text, LaunchScanText.Text }.Any(text => text.StartsWith('!')))
        {
            await Task.Delay(TimeSpan.FromSeconds(60));
            LaunchStepsStrip.Visibility = Visibility.Collapsed;
        }
    }

    private async Task RunLaunchAutomationCoreAsync()
    {
        var automate = Host.SettingsPage.AutomateOnLaunchCheckBox.IsChecked == true;
        var tuneEachSession = Host.SettingsPage.AutoTuneEachSessionCheckBox.IsChecked == true;
        if (!Host.LaunchAutomationEnabled || (!automate && !tuneEachSession && !Host.IsFirstRun))
        {
            return;
        }

        LaunchStepsStrip.Visibility = Visibility.Visible;
        SetLaunchStep(LaunchSyncText, LaunchStep.Waiting, "Account sync");
        SetLaunchStep(LaunchTuneText, LaunchStep.Waiting, "Auto-Tune");
        SetLaunchStep(LaunchScanText, LaunchStep.Waiting, "Market scan");
        AddLog("Launch: account sync, Auto-Tune and market scan (Settings → Trading & Portfolio to change).");

        if (!automate)
        {
            SetLaunchStep(LaunchSyncText, LaunchStep.Skipped, "Account sync off");
        }
        else if (string.IsNullOrWhiteSpace(Host.ResolveConfiguredApiKey()) || Host.LedgerStore is null)
        {
            SetLaunchStep(LaunchSyncText, LaunchStep.Skipped, "No API key: account not synced");
        }
        else
        {
            SetLaunchStep(LaunchSyncText, LaunchStep.Running, "Syncing account…");
            SetLaunchStep(LaunchSyncText, await Host.IncomePage.SyncPortfolioCoreAsync() ? LaunchStep.Done : LaunchStep.Failed,
                Host.IncomePage.LatestEquitySnapshot is { } snap ? $"Account synced ({MarketScanItem.FormatCoins(snap.WalletCopper)} wallet)" : "Account sync failed");
        }

        SetLaunchStep(LaunchTuneText, LaunchStep.Running, "Auto-Tuning…");
        var (tuneStep, tuneText) = await CheckAutoTuneOnLaunchAsync();
        SetLaunchStep(LaunchTuneText, tuneStep, tuneText);

        if (!automate)
        {
            SetLaunchStep(LaunchScanText, LaunchStep.Skipped, "Market scan off");
            return;
        }

        if (Host.LastWorkerState is not (WorkerState.Idle or WorkerState.Stopped))
        {
            SetLaunchStep(LaunchScanText, LaunchStep.Skipped, "Session already running");
            return; // a session was started meanwhile; leave its list alone
        }

        SetLaunchStep(LaunchScanText, LaunchStep.Running, "Scanning the market (~1 min)…");
        if (await Host.ScannerPage.RunMarketScanCoreAsync(useCachedPrices: false))
        {
            var listNote = RefreshSessionListAfterLaunchScan();
            SetLaunchStep(LaunchScanText, LaunchStep.Done, $"{Host.ScannerPage.ScanResults.Count} flips found{listNote}");
        }
        else
        {
            SetLaunchStep(LaunchScanText, LaunchStep.Failed, "Market scan failed (see Market Scanner)");
        }
    }

    private async Task<(LaunchStep Step, string Text)> CheckAutoTuneOnLaunchAsync()
    {
        var wallet = Host.IncomePage.LatestEquitySnapshot?.WalletCopper ?? 0;
        var key = Host.ResolveConfiguredApiKey();
        if (wallet <= 0 && !string.IsNullOrWhiteSpace(key))
        {
            try
            {
                using var cts = Host.CreateApiCancellationTokenSource();
                wallet = (await new Gw2OfficialDataProvider(Host.Gw2HttpClient, key).GetBalanceAsync(cts.Token))?.Copper ?? 0;
            }
            catch (Exception ex)
            {
                AddLog($"Launch: wallet read failed ({ex.Message}); Auto-Tune skipped.");
                return (LaunchStep.Failed, "Auto-Tune: wallet unreadable");
            }
        }

        if (wallet <= 0)
        {
            return (LaunchStep.Skipped, "Auto-Tune: no wallet yet (add an API key)"); // never tune from a guess
        }

        var preset = TradingStrategyPresets.RecommendForCapital(wallet);
        if (Host.SettingsPage.AutoTuneEachSessionCheckBox.IsChecked == true || (Host.IsFirstRun && Host.SettingsPage.LastAutoTunedPreset is null))
        {
            var tune = await Host.SettingsPage.RunAutoTuneAsync(feedbackButton: null);
            return (LaunchStep.Done, $"Auto-Tuned: {ShortPresetName(tune.Preset.Name)}, Max Buy {tune.Preset.MaxBuyGold:0.#}g");
        }

        if (Host.SettingsPage.LastAutoTunedPreset is not null && !string.Equals(Host.SettingsPage.LastAutoTunedPreset, preset.Name, StringComparison.OrdinalIgnoreCase))
        {
            // With Auto-Tune each session off, the operator tunes by hand: only suggest.
            var hint = $"Your wallet ({MarketScanItem.FormatCoins(wallet)}) now fits the '{preset.Name}' preset (last tuned: '{Host.SettingsPage.LastAutoTunedPreset}'). Click Auto-Tune to switch.";
            Host.ScannerPage.ScanStatusText.Text = hint;
            AddLog("Launch: " + hint);
            return (LaunchStep.Skipped, $"Auto-Tune off: '{ShortPresetName(preset.Name)}' fits now");
        }

        return (LaunchStep.Skipped, "Auto-Tune off");
    }

    private static string ShortPresetName(string name)
    {
        var paren = name.IndexOf(" (", StringComparison.Ordinal);
        return paren > 0 ? name[..paren] : name;
    }

    /// <summary>Returns a short note for the Startup strip when the session list was replaced.</summary>
    private string RefreshSessionListAfterLaunchScan()
    {
        if (Host.ScannerPage.ScanResults.Count == 0)
        {
            return string.Empty;
        }

        var listPath = NullIfBlank(Host.SettingsPage.CandidatesPathBox.Text);
        if (listPath is null)
        {
            // No list at all: trade the fresh scan, unless a Custom Shopping List is the operator's whole plan.
            if (Host.SettingsPage.UseCustomListOnStartCheckBox.IsChecked == true && Host.ListsPage.CustomShoppingList.Count > 0)
            {
                return string.Empty;
            }

            Host.ScannerPage.BindScanResultsToSession();
            AddLog($"Launch: no session list was set, so the fresh scan ({Host.ScannerPage.ScanResults.Count} items) is now the session list.");
            return " · now the session list";
        }

        if (!string.Equals(Path.GetFullPath(listPath), Path.GetFullPath(MainWindow.ScannedCandidatesPath), StringComparison.OrdinalIgnoreCase))
        {
            return " · your own list kept";
        }

        var intervalHours = Host.ScannerPage.ReadAutoRescanHours();
        var age = File.Exists(MainWindow.ScannedCandidatesPath)
            ? DateTime.UtcNow - File.GetLastWriteTimeUtc(MainWindow.ScannedCandidatesPath)
            : TimeSpan.MaxValue;
        if (intervalHours > 0 && age >= TimeSpan.FromHours((double)intervalHours))
        {
            Host.ScannerPage.BindScanResultsToSession();
            AddLog($"Launch: the scanned session list was {FormatAge(age)} old; replaced with today's scan ({Host.ScannerPage.ScanResults.Count} items).");
            return " · session list refreshed";
        }

        return string.Empty;
    }

    private static string FormatAge(TimeSpan age) =>
        age == TimeSpan.MaxValue ? "missing" : age.TotalDays >= 1 ? $"{age.TotalDays:0.#} days" : $"{age.TotalHours:0.#} h";

    internal string UpdateStatusCard(WorkerState state, string? detail)
    {
        var (label, brushKey) = state switch
        {
            WorkerState.ProcessingTrading or WorkerState.TradingPostReady => ("Trading", "SuccessBrush"),
            WorkerState.Starting => ("Starting", "AccentBrush"),
            WorkerState.LaunchingClient or WorkerState.WaitingForLogin => ("Launching", "AccentBrush"),
            WorkerState.Fishing => ("Fishing", "SuccessBrush"),
            WorkerState.Stopping => ("Stopping", "TextMutedBrush"),
            WorkerState.Faulted => ("Faulted", "DangerBrush"),
            WorkerState.Paused => ("Paused", "WarningBrush"),
            WorkerState.Recovering => ("Recovering", "WarningBrush"),
            WorkerState.NeedsAttention => ("Needs attention", "DangerBrush"),
            WorkerState.Stopped => ("Stopped", "TextMutedBrush"),
            WorkerState.Idle => ("Idle", "TextMutedBrush"),
            _ => (state.ToString(), "TextMutedBrush")
        };
        var brush = ResolveBrush(brushKey, Brushes.Gray);
        StatusStateText.Text = label;
        StatusStateDot.Fill = brush;
        StatusCard.ToolTip = string.IsNullOrWhiteSpace(detail) ? label : $"{label}: {detail}";
        return label;
    }

    /// <summary>"72 buys and 14 sells. 7 buys are behind the top bid. 12 sells are behind the lowest ask." -> headline + detail.</summary>
    internal void UpdateOrdersCard(string? orderBook, bool buysPaused)
    {
        var text = orderBook ?? string.Empty;
        var counts = System.Text.RegularExpressions.Regex.Match(text, @"(\d+) buys? and (\d+) sells?");
        var behindBuys = System.Text.RegularExpressions.Regex.Match(text, @"(\d+) buys? (?:is|are) behind");
        var behindSells = System.Text.RegularExpressions.Regex.Match(text, @"(\d+) sells? (?:is|are) behind");
        OrdersHeadlineText.Text = counts.Success ? $"{counts.Groups[1].Value} buys · {counts.Groups[2].Value} sells" : "—";
        var detail = new List<string>();
        if (buysPaused) detail.Add("buys paused (gold floor)");
        if (behindBuys.Success) detail.Add($"{behindBuys.Groups[1].Value} outbid");
        if (behindSells.Success) detail.Add($"{behindSells.Groups[1].Value} undercut");
        OrdersDetailText.Text = detail.Count > 0 ? string.Join(" · ", detail) : counts.Success ? "all at the top of the book" : "No orders reported";
        OrdersCard.ToolTip = string.IsNullOrWhiteSpace(text) ? null : text;
    }

    internal void ShowSwapStatus(string swapText)
    {
        AfkSwapStatusText.Text = swapText;
        ScheduleHeadlineText.Text = ShortSwapHeadline(AfkSwapStatusText.Text);
        ScheduleCard.ToolTip = AfkSwapStatusText.Text;
    }

    /// <summary>"Next AFK swap (random character) in 61m at 7:33 CH." -> "Swap in 61m".</summary>
    private static string ShortSwapHeadline(string swapText)
    {
        if (swapText.Contains("disabled", StringComparison.OrdinalIgnoreCase)) return "No AFK swap";
        if (swapText.Contains("due now", StringComparison.OrdinalIgnoreCase)) return "Swap due";
        var minutes = System.Text.RegularExpressions.Regex.Match(swapText, @"in (\d+)m");
        if (!minutes.Success || !int.TryParse(minutes.Groups[1].Value, out var total)) return "AFK swap on";
        return total >= 60 ? $"Swap in {total / 60}h {total % 60:00}m" : $"Swap in {total}m";
    }

    internal void UpdateSessionClockDisplay()
    {
        if (SessionClockText is null || SessionClockIcon is null || SessionClockPillBorder is null)
        {
            return;
        }

        if (Host.SessionStartedAtUtc is not { } startedAt)
        {
            SessionClockText.Text = "00:00:00";
            SessionClockIcon.Foreground = ResolveBrush("AccentBrush", Brushes.DodgerBlue);
            SessionClockPillBorder.ToolTip = "Elapsed trade session clock (starts with each session).";
            Host.UpdateSidebarStatusTooltip();
            return;
        }

        var elapsed = DateTimeOffset.UtcNow - startedAt;
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        var hours = (int)elapsed.TotalHours;
        SessionClockText.Text = $"{hours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";

        if (Host.SessionExpiresAtUtc is { } expiresAt)
        {
            var remaining = expiresAt - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                SessionClockIcon.Foreground = ResolveBrush("WarningBrush", Brushes.DarkOrange);
                SessionCapStatusText.Text = "Session cap reached";
                SessionClockPillBorder.ToolTip = $"Elapsed: {SessionClockText.Text} • Max session cap reached (finalizing)";
            }
            else
            {
                var isFinalFiveMinutes = remaining <= TimeSpan.FromMinutes(5);
                SessionClockIcon.Foreground = isFinalFiveMinutes
                    ? ResolveBrush("WarningBrush", Brushes.DarkOrange)
                    : ResolveBrush("SuccessBrush", Brushes.SeaGreen);
                var remHours = (int)remaining.TotalHours;
                SessionCapStatusText.Text = remHours > 0
                    ? $"Stops in {remHours}h {remaining.Minutes:00}m ({expiresAt.ToLocalTime():t})"
                    : $"Stops in {Math.Ceiling(remaining.TotalMinutes):0}m ({expiresAt.ToLocalTime():t})";
                SessionClockPillBorder.ToolTip = $"Elapsed: {SessionClockText.Text} • {SessionCapStatusText.Text}";
            }
        }
        else
        {
            SessionClockIcon.Foreground = ResolveBrush("SuccessBrush", Brushes.SeaGreen);
            SessionClockPillBorder.ToolTip = $"Elapsed: {SessionClockText.Text} • No time cap";
        }

        Host.UpdateSidebarStatusTooltip();
    }

    internal void AddLog(string message, string? rawMessage = null)
    {
        var now = DateTime.Now;
        var raw = rawMessage ?? message;
        _logEntries.Add((now, message, raw));
        if (_logEntries.Count > 500)
        {
            _logEntries.RemoveAt(0);
        }

        var display = ShowRawLogCheckBox.IsChecked == true ? raw : message;
        LogList.Items.Add($"[{now:T}] {display}");
        if (LogList.Items.Count > 500)
        {
            LogList.Items.RemoveAt(0);
        }

        LogList.ScrollIntoView(LogList.Items[^1]);
        UpdateLogCount();
    }

    private void RebuildLogList()
    {
        var showRaw = ShowRawLogCheckBox.IsChecked == true;
        LogList.Items.Clear();
        foreach (var entry in _logEntries)
        {
            LogList.Items.Add($"[{entry.Timestamp:T}] {(showRaw ? entry.Raw : entry.Human)}");
        }

        if (LogList.Items.Count > 0)
        {
            LogList.ScrollIntoView(LogList.Items[^1]);
        }

        UpdateLogCount();
    }

    private void UpdateLogCount() => LogCountText.Text = $"{LogList.Items.Count} events";

    private static string ResolveTemplateRootDirectory()
    {
        const string relative = "assets/templates/tradingpost";
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 8 && current is not null; depth++, current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, relative);
            if (Directory.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return Path.GetFullPath(relative, AppContext.BaseDirectory);
    }
}
