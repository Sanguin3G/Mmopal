using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Mmopal.Contracts;
using Mmopal.Desktop;
using Mmopal.Gw2;

namespace Mmopal.Domain.Tests;

public sealed class MainWindowSmokeTests
{
    [Fact]
    public void Main_window_defaults_to_replay_and_start_stop_change_status()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = new App();
                app.InitializeComponent();
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                app.ApplyTheme("Light");

                var supervisor = new ScriptedSupervisor();
                var saved = new List<TradingLimits>();
                var savedClientSettings = new List<Gw2ClientSettings>();
                var savedOpSettings = new List<OperatorSettings>();
                var appClosed = false;
                int? shutdownRequestedSeconds = null;
                var preparer = new FakeClientPreparer();
                var window = new MainWindow(
                    supervisor,
                    () => new TradingLimits(0, null),
                    saved.Add,
                    () => new Gw2ClientSettings(ExecutablePath: @"C:\Games\Guild Wars 2\Gw2-64.exe", SwapCharacterIntervalMinutes: 120),
                    savedClientSettings.Add,
                    preparer,
                    () => @"C:\Games\Guild Wars 2\Gw2-64.exe",
                    () => new OperatorSettings(ThemeMode: "Dark"),
                    savedOpSettings.Add,
                    loadCustomList: () => [new ItemCandidate("Mystic Coin", "Rare", 5, BuyPriceCopper: 10_000, SellPriceCopper: 15_000)],
                    loadBlacklist: () => ["Skip Me"],
                    requestAppClose: () => appClosed = true,
                    executeSystemShutdown: seconds => shutdownRequestedSeconds = seconds);

                Assert.True(app.IsDarkTheme);
                var bgBrush = Assert.IsType<SolidColorBrush>(window.Background);
                Assert.Equal(Color.FromRgb(0x0D, 0x11, 0x17), bgBrush.Color);
                Assert.IsType<ComboBox>(window.FindName("BotSelector"));
                Assert.IsType<TextBox>(window.FindName("MinimumProfitBox"));
                Assert.IsType<TextBox>(window.FindName("MaximumBuyGoldBox"));
                Assert.IsType<PasswordBox>(window.FindName("ApiKeyPasswordBox"));
                Assert.IsType<DataGrid>(window.FindName("ScanResultsGrid"));
                Assert.IsType<DataGrid>(window.FindName("CustomShoppingListGrid"));
                Assert.IsType<DataGrid>(window.FindName("ItemPnlGrid"));
                Assert.IsType<DataGrid>(window.FindName("ActiveOrdersGrid"));
                Assert.IsType<DataGrid>(window.FindName("LedgerHistoryGrid"));
                Assert.IsType<TextBox>(window.FindName("MaxSessionHoursBox"));
                Assert.IsType<TextBox>(window.FindName("ParkedCharactersBox"));
                Assert.IsType<TextBox>(window.FindName("InteractKeyBox"));
                Assert.IsType<TextBox>(window.FindName("ScanMaxRoiBox"));
                var gw2PathBox = Assert.IsType<TextBox>(window.FindName("Gw2ExecutablePathBox"));
                Assert.Equal(@"C:\Games\Guild Wars 2\Gw2-64.exe", gw2PathBox.Text);
                var swapHoursBox = Assert.IsType<TextBox>(window.FindName("SwapCharacterHoursBox"));
                Assert.Equal("2", swapHoursBox.Text);
                var candidatesPathBox = Assert.IsType<TextBox>(window.FindName("CandidatesPathBox"));
                candidatesPathBox.Text = "D:/tmp/candidates.json";

                var source = Assert.IsType<ComboBox>(window.FindName("SourceSelector"));
                var selectedSource = Assert.IsType<ComboBoxItem>(source.SelectedItem);
                Assert.Equal("replay", selectedSource.Tag);
                var screenSource = Assert.IsType<ComboBoxItem>(source.Items[1]);
                Assert.Equal("screen", screenSource.Tag);
                Assert.Equal("Screen observe", screenSource.Content);

                var mode = Assert.IsType<ComboBox>(window.FindName("ModeSelector"));
                var selectedMode = Assert.IsType<ComboBoxItem>(mode.SelectedItem);
                Assert.Equal("DryRun", selectedMode.Tag);
                var live = Assert.IsType<ComboBoxItem>(mode.Items[2]);
                Assert.Equal("Live", live.Tag);
                Assert.Equal("Live (Armed trading)", live.Content);

                var status = Assert.IsType<TextBlock>(window.FindName("StatusText"));
                Assert.Equal("Idle — waiting for a session", status.Text);
                Assert.Equal("00:00:00", Assert.IsType<TextBlock>(window.FindName("SessionClockText")).Text);

                window.Show();
                Assert.True(window.IsVisible);

                // Verify sidebar navigation toggles all 7 views cleanly.
                Assert.IsType<RadioButton>(window.FindName("NavIncome")).IsChecked = true;
                Assert.Equal(Visibility.Visible, Assert.IsType<Grid>(window.FindName("IncomeView")).Visibility);
                Assert.Equal(Visibility.Collapsed, Assert.IsType<Grid>(window.FindName("SessionView")).Visibility);

                Assert.IsType<RadioButton>(window.FindName("NavReports")).IsChecked = true;
                Assert.Equal(Visibility.Visible, Assert.IsType<Grid>(window.FindName("ReportsView")).Visibility);

                Assert.IsType<RadioButton>(window.FindName("NavScanner")).IsChecked = true;
                Assert.Equal(Visibility.Visible, Assert.IsType<Grid>(window.FindName("ScannerView")).Visibility);

                Assert.IsType<RadioButton>(window.FindName("NavLists")).IsChecked = true;
                Assert.Equal(Visibility.Visible, Assert.IsType<Grid>(window.FindName("ListsView")).Visibility);

                Assert.IsType<RadioButton>(window.FindName("NavSettings")).IsChecked = true;
                Assert.Equal(Visibility.Visible, Assert.IsType<Grid>(window.FindName("SettingsView")).Visibility);

                Assert.IsType<RadioButton>(window.FindName("NavGuide")).IsChecked = true;
                Assert.Equal(Visibility.Visible, Assert.IsType<Grid>(window.FindName("GuideView")).Visibility);

                Assert.IsType<RadioButton>(window.FindName("NavSession")).IsChecked = true;
                Assert.Equal(Visibility.Visible, Assert.IsType<Grid>(window.FindName("SessionView")).Visibility);

                RequireButton(window, "Launch & snap GW2").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var waitUntil = DateTime.UtcNow.AddSeconds(2);
                while (status.Text != "Positioned Guild Wars 2 at top-left (0, 0) sized 1024x768." && DateTime.UtcNow < waitUntil)
                {
                    PumpDispatcher();
                    Thread.Sleep(10);
                }

                Assert.Single(preparer.Calls);
                Assert.True(preparer.Calls[0].AllowLaunch);
                Assert.Equal(@"C:\Games\Guild Wars 2\Gw2-64.exe", preparer.Calls[0].Settings.ExecutablePath);
                Assert.Equal("Positioned Guild Wars 2 at top-left (0, 0) sized 1024x768.", status.Text);

                RequireButton(window, "Start session").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpDispatcher();
                Assert.Equal("Session started", status.Text);
                Assert.Equal("Starting", Assert.IsType<TextBlock>(window.FindName("StatusStateText")).Text);
                Assert.Equal(WorkerCommandTypes.Start, supervisor.Commands[0].Type);
                Assert.Equal(BotKind.TradingPost, supervisor.Commands[0].Bot);
                Assert.Equal(RunMode.DryRun, supervisor.Commands[0].Mode);
                Assert.Equal("balanced", supervisor.Commands[0].Profile);
                var payload = supervisor.Commands[0].Payload;
                Assert.NotNull(payload);
                var replay = JsonSerializer.Deserialize<TradingReplayRequest>(
                    payload,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web));
                Assert.NotNull(replay);
                Assert.Equal("replay", replay.Source);
                Assert.Equal("D:/tmp/candidates.json", replay.CandidatesPath);
                Assert.NotNull(replay.ClientSettings);
                Assert.Equal(@"C:\Games\Guild Wars 2\Gw2-64.exe", replay.ClientSettings.ExecutablePath);
                Assert.Equal(120, replay.ClientSettings.SwapCharacterIntervalMinutes);
                Assert.Equal(600, replay.ClientSettings.MaxSessionDurationMinutes);
                Assert.Equal(4, replay.ClientSettings.ParkedCharacterCount);
                Assert.Equal("F", replay.ClientSettings.InteractKeybind);
                Assert.Equal(Gw2ClientSettings.PlatformArenaNet, replay.ClientSettings.LaunchPlatform);
                Assert.Equal(3, replay.ClientSettings.MaxRecoveryRetries);
                Assert.True(replay.ClientSettings.AutoRestartGameOnFailure);
                Assert.Equal(2, replay.ClientSettings.MaxGameRestartsPerSession);
                Assert.True(replay.UsePortfolioIntelligence);
                Assert.Single(replay.EffectiveCandidates);
                Assert.Equal("Mystic Coin", replay.EffectiveCandidates[0].ItemName);
                Assert.Single(replay.EffectiveBlacklist);
                Assert.Equal("Skip Me", replay.EffectiveBlacklist[0]);
                Assert.Equal(0, saved[0].MinimumProfitPercentage);
                Assert.Null(saved[0].MaximumBuyPriceGold);
                Assert.NotEmpty(savedClientSettings);
                Assert.NotEmpty(savedOpSettings);

                RequireButton(window, "Stop").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpDispatcher();
                Assert.Equal("Session stopped", status.Text);
                Assert.Equal("Stopped", Assert.IsType<TextBlock>(window.FindName("StatusStateText")).Text);
                Assert.Equal(WorkerCommandTypes.Stop, supervisor.Commands[1].Type);
                Assert.Equal(2, supervisor.Commands.Count);

                supervisor.Raise(new WorkerEvent(
                    WorkerEventTypes.Health,
                    """{"source":"replay","ui":{"tradingPostOpen":true,"itemWindowOpen":true},"visiblePrice":{"price":{"copper":4321}},"timings":{"api":3},"orderBook":"1 buy and 1 sell. 1 buy is behind the top bid.","characterSwap":"Next AFK swap (random character) in 120m."}"""));
                PumpDispatcher();
                Assert.Equal("replay", Assert.IsType<TextBlock>(window.FindName("ObservationSourceText")).Text);
                Assert.Equal(
                    "Trading Post: open; item: open",
                    Assert.IsType<TextBlock>(window.FindName("ObservationUiText")).Text);
                Assert.Equal("4321", Assert.IsType<TextBlock>(window.FindName("ObservationPriceText")).Text);
                Assert.Contains("api", Assert.IsType<TextBlock>(window.FindName("ObservationTimingText")).Text, StringComparison.Ordinal);
                Assert.Equal(
                    "1 buy and 1 sell. 1 buy is behind the top bid.",
                    Assert.IsType<TextBlock>(window.FindName("ObservationOrdersText")).Text);
                Assert.Contains("Next AFK swap", Assert.IsType<TextBlock>(window.FindName("AfkSwapStatusText")).Text, StringComparison.Ordinal);

                var armLiveBox = Assert.IsType<CheckBox>(window.FindName("ArmLiveInputCheckBox"));
                var autoSellBox = Assert.IsType<CheckBox>(window.FindName("AutoSellInventoryCheckBox"));
                var safeModePillText = Assert.IsType<TextBlock>(window.FindName("SafeModePillText"));
                Assert.False(armLiveBox.IsChecked == true);
                Assert.False(autoSellBox.IsChecked == true);

                mode.SelectedIndex = 2; // Live (Armed trading) arms live input by itself
                PumpDispatcher();
                Assert.True(armLiveBox.IsChecked == true);
                Assert.Contains("ARMED", safeModePillText.Text, StringComparison.OrdinalIgnoreCase);
                Assert.Equal("screen", Assert.IsType<ComboBoxItem>(source.SelectedItem).Tag); // Live pairs with Screen observe

                source.SelectedIndex = 0; // Replay data cannot drive live clicks
                PumpDispatcher();
                Assert.Equal("DryRun", Assert.IsType<ComboBoxItem>(mode.SelectedItem).Tag);
                Assert.False(armLiveBox.IsChecked == true); // leaving Live disarms

                // A hand-set gold floor survives Auto-Tune (it used to be reset from 25g to 80g on Start session).
                var goldFloorBox = Assert.IsType<TextBox>(window.FindName("GoldThresholdNoBuyBox"));
                goldFloorBox.Text = "25";
                PumpDispatcher();
                Assert.Equal(Visibility.Visible, Assert.IsType<DockPanel>(window.FindName("AutoTuneKeepsPanel")).Visibility);
                window.ApplyStrategyPresetToSettings(TradingStrategyPresets.All[1]);
                Assert.Equal("25", goldFloorBox.Text);
                Assert.Equal(TradingStrategyPresets.All[1].MaxBuyGold.ToString(System.Globalization.CultureInfo.CurrentCulture),
                    Assert.IsType<TextBox>(window.FindName("MaximumBuyGoldBox")).Text);

                armLiveBox.IsChecked = false;
                mode.SelectedIndex = 0;
                PumpDispatcher();

                var startBtn = Assert.IsType<Button>(window.FindName("StartSessionButton"));
                var pauseBtn = Assert.IsType<Button>(window.FindName("PauseSessionButton"));
                var resumeBtn = Assert.IsType<Button>(window.FindName("ResumeSessionButton"));
                var stopBtn = Assert.IsType<Button>(window.FindName("StopSessionButton"));

                window.UpdateSessionButtonStates(WorkerState.Idle);
                Assert.True(startBtn.IsEnabled);
                Assert.False(stopBtn.IsEnabled);

                window.UpdateSessionButtonStates(WorkerState.ProcessingTrading);
                Assert.False(startBtn.IsEnabled);
                Assert.Equal("Running...", startBtn.Content);
                Assert.True(pauseBtn.IsEnabled);
                Assert.True(stopBtn.IsEnabled);
                Assert.False(mode.IsEnabled);
                Assert.False(autoSellBox.IsEnabled);

                window.UpdateSessionButtonStates(WorkerState.Paused);
                Assert.Equal("Paused", startBtn.Content);
                Assert.False(pauseBtn.IsEnabled);
                Assert.True(resumeBtn.IsEnabled);
                Assert.True(stopBtn.IsEnabled);

                window.UpdateSessionButtonStates(WorkerState.Stopped);
                Assert.True(startBtn.IsEnabled);
                Assert.Equal("Start session", startBtn.Content);

                var timeframeCombo = Assert.IsType<ComboBox>(window.FindName("IncomeTimeframeSelector"));
                Assert.True(timeframeCombo.MinWidth >= 120);
                Assert.IsType<DataGrid>(window.FindName("ApiKeysGrid"));
                Assert.IsType<ComboBox>(window.FindName("SidebarAccountSelector"));

                var latencyCombo = Assert.IsType<ComboBox>(window.FindName("LatencyProfileSelector"));
                latencyCombo.SelectedIndex = 0; // Low ping
                PumpDispatcher();
                Assert.Equal("3000", Assert.IsType<TextBox>(window.FindName("UiLoadWaitMsBox")).Text);
                Assert.Equal("30", Assert.IsType<TextBox>(window.FindName("ApiTimeoutSecondsBox")).Text);

                Assert.NotNull(window.Icon);
                Assert.IsType<ComboBox>(window.FindName("LaunchPlatformSelector"));
                Assert.IsType<TextBox>(window.FindName("MaxRecoveryRetriesBox"));
                Assert.IsType<TextBox>(window.FindName("MaxGameRestartsBox"));
                Assert.IsType<CheckBox>(window.FindName("AutoRestartOnFailureCheckBox"));
                Assert.IsType<CheckBox>(window.FindName("UsePortfolioIntelligenceCheckBox"));
                Assert.IsType<TabControl>(window.FindName("SettingsTabControl"));
                Assert.IsType<TextBlock>(window.FindName("PortfolioInsightsText"));
                Assert.IsType<TextBlock>(window.FindName("SessionPaceSummaryText"));
                var scannerCoreGrid = Assert.IsType<UniformGrid>(window.FindName("ScannerCoreFiltersGrid"));
                var scannerAdvGrid = Assert.IsType<UniformGrid>(window.FindName("ScannerAdvancedFiltersGrid"));

                var autoDockBox = Assert.IsType<CheckBox>(window.FindName("AutoDockBesideGw2CheckBox"));
                Assert.True(autoDockBox.IsChecked == true);
                autoDockBox.IsChecked = false; // prevent actual screen resize while verifying layout API directly

                var sidebarCol = Assert.IsType<ColumnDefinition>(window.FindName("SidebarColumn"));
                var sidebarStatusBorder = Assert.IsType<Border>(window.FindName("SidebarStatusBorder"));
                var sidebarStatusDot = Assert.IsType<System.Windows.Shapes.Ellipse>(window.FindName("SidebarStatusDot"));
                var incomeActionsPanel = Assert.IsType<WrapPanel>(window.FindName("IncomeHeaderActionsPanel"));
                var listsLeftCol = Assert.IsType<ColumnDefinition>(window.FindName("ListsLeftCol"));
                var listsRightCol = Assert.IsType<ColumnDefinition>(window.FindName("ListsRightCol"));
                var sessionClockText = Assert.IsType<TextBlock>(window.FindName("SessionClockText"));
                Assert.Matches(@"^\d{2}:\d{2}:\d{2}$", sessionClockText.Text);
                Assert.NotNull(sidebarStatusBorder.ToolTip);

                Assert.IsType<CheckBox>(window.FindName("AutoCloseGameOnCapCheckBox"));
                Assert.IsType<CheckBox>(window.FindName("AutoCloseAppOnCapCheckBox"));
                Assert.IsType<CheckBox>(window.FindName("ShutdownPcOnCapCheckBox"));
                Assert.IsType<TextBox>(window.FindName("PcShutdownCountdownBox"));
                Assert.IsType<CheckBox>(window.FindName("SaveSessionReportsCheckBox"));
                Assert.IsType<TextBox>(window.FindName("ReportMinDurationBox"));
                Assert.IsType<ListBox>(window.FindName("SessionReportsListBox"));

                var selectorsGrid = Assert.IsType<UniformGrid>(window.FindName("SessionSelectorsGrid"));
                var sessionKpiGrid = Assert.IsType<UniformGrid>(window.FindName("SessionKpiGrid"));
                var incomeKpiGrid = Assert.IsType<UniformGrid>(window.FindName("IncomeKpiGrid"));
                var footerExpanded = Assert.IsType<StackPanel>(window.FindName("SidebarFooterExpanded"));

                window.ApplyResponsiveLayout(540);
                Assert.Equal(64, sidebarCol.Width.Value);
                Assert.Equal(40, sidebarStatusBorder.Width);
                Assert.Equal(40, sidebarStatusBorder.Height);
                Assert.Equal(new Thickness(0), sidebarStatusDot.Margin);
                Assert.Equal(1, Grid.GetRow(incomeActionsPanel));
                Assert.Equal(0, listsRightCol.Width.Value);
                Assert.Equal(2, selectorsGrid.Columns);
                Assert.Equal(2, sessionKpiGrid.Columns);
                Assert.Equal(2, incomeKpiGrid.Columns);
                Assert.Equal(2, scannerCoreGrid.Columns);
                Assert.Equal(2, scannerAdvGrid.Columns);
                Assert.Equal(Visibility.Collapsed, footerExpanded.Visibility);

                window.ApplyResponsiveLayout(1300);
                Assert.Equal(228, sidebarCol.Width.Value);
                Assert.True(double.IsNaN(sidebarStatusBorder.Width));
                Assert.Equal(0, Grid.GetRow(incomeActionsPanel));
                Assert.Equal(1.2, listsLeftCol.Width.Value);
                Assert.Equal(1, listsRightCol.Width.Value);
                Assert.Equal(4, selectorsGrid.Columns);
                Assert.Equal(4, sessionKpiGrid.Columns);
                Assert.Equal(4, incomeKpiGrid.Columns);
                Assert.Equal(4, scannerCoreGrid.Columns);
                Assert.Equal(4, scannerAdvGrid.Columns);
                Assert.Equal(Visibility.Visible, footerExpanded.Visibility);

                Assert.False(appClosed);
                Assert.Null(shutdownRequestedSeconds);
                window.Close();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(20)))
        {
            throw new TimeoutException("The window smoke test did not finish.");
        }

        Assert.True(failure is null, failure?.ToString());
    }

    private static Button RequireButton(DependencyObject root, string content)
    {
        Button? match = null;
        Walk(root);
        Assert.NotNull(match);
        return match!;

        void Walk(DependencyObject node)
        {
            if (node is Button button && button.Content as string == content)
            {
                match = button;
            }

            foreach (var child in LogicalTreeHelper.GetChildren(node))
            {
                if (child is DependencyObject dependency)
                {
                    Walk(dependency);
                }
            }
        }
    }

    private static void PumpDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    private sealed class ScriptedSupervisor : IDesktopWorkerSupervisor
    {
        private static readonly DateTimeOffset Timestamp = new(2024, 1, 2, 3, 4, 5, TimeSpan.Zero);

        public List<WorkerCommand> Commands { get; } = [];

        public event EventHandler<WorkerEvent>? EventReceived;

        public void Raise(WorkerEvent workerEvent) => EventReceived?.Invoke(this, workerEvent);

        public Task<WorkerEvent> SendAsync(WorkerCommand command, CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            var status = command.Type == WorkerCommandTypes.Stop
                ? new WorkerStatus(WorkerState.Stopped, BotKind.TradingPost, RunMode.DryRun, "balanced", "Stop", Timestamp, "Session stopped")
                : new WorkerStatus(WorkerState.Starting, BotKind.TradingPost, RunMode.DryRun, "balanced", "Start", Timestamp, "Session started");
            return Task.FromResult(new WorkerEvent(WorkerEventTypes.Status, JsonSerializer.Serialize(status)));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeClientPreparer : IGw2ClientPreparer
    {
        public List<(Gw2ClientSettings Settings, bool AllowLaunch)> Calls { get; } = [];

        public Task<Gw2ClientPrepareResult> PrepareAsync(
            Gw2ClientSettings settings,
            bool allowLaunch,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((settings, allowLaunch));
            return Task.FromResult(new Gw2ClientPrepareResult(
                WindowReady: true,
                Launched: false,
                AlreadyRunning: true,
                Message: "Positioned Guild Wars 2 at top-left (0, 0) sized 1024x768."));
        }
    }
}
