using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Mmopal.Contracts;
using Mmopal.Gw2;
using Mmopal.Infrastructure;
using Mmopal.Native;
using Mmopal.Worker;
using static Mmopal.Desktop.UiHelpers;

namespace Mmopal.Desktop;

public partial class MainWindow : Window
{
    internal static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web);
    internal IDesktopWorkerSupervisor Supervisor { get; }
    internal Func<TradingLimits> ReadSavedLimits { get; }
    internal Action<TradingLimits> SaveLimits { get; }
    internal Func<Gw2ClientSettings> ReadSavedClientSettings { get; }
    internal Action<Gw2ClientSettings> SaveClientSettings { get; }
    internal Func<OperatorSettings> ReadSavedOperatorSettings { get; }
    internal Action<OperatorSettings> SaveOperatorSettings { get; }
    internal IGw2ClientPreparer ClientPreparer { get; }
    internal Func<string?> DetectGw2Path { get; }
    internal Func<IReadOnlyList<Gw2InstallCandidate>> DiscoverGw2Installs { get; }
    internal Func<DisplayScaleInfo> DetectDisplayScale { get; }
    internal TradingLedgerStore? LedgerStore { get; }
    internal SessionReportStore? SessionReportStore { get; }
    internal IGw2MarketScanner MarketScanner { get; }
    private readonly Func<Gw2ApiKeyVaultData> _loadApiKeyVault;
    internal Action<Gw2ApiKeyVaultData> SaveApiKeyVault { get; }
    private readonly Action _requestAppClose;
    private readonly Action<int> _executeSystemShutdown;
    private readonly GlobalHotkeyService _hotkeys = new();
    internal HttpClient Gw2HttpClient { get; } = new() { BaseAddress = new Uri("https://api.guildwars2.com/"), Timeout = TimeSpan.FromSeconds(120) };
    private readonly DispatcherTimer _cycleTimer;
    private readonly DispatcherTimer _sessionClockTimer;

    internal Gw2ApiKeyVaultData ApiKeyVault { get; set; } = new(null, Array.Empty<Gw2ApiKeyEntry>());
    internal bool ReadinessBannerDismissed { get; set; }
    internal bool HasVerifiedApiKey { get; set; }
    private Rect? _preDockBounds;
    private WindowState? _preDockWindowState;
    private bool _isSideDocked;
    internal DateTimeOffset? SessionStartedAtUtc { get; set; }
    internal DateTimeOffset? SessionExpiresAtUtc { get; set; }
    internal string? LastHandledCapReportId { get; set; }

    internal WorkerState LastWorkerState { get; private set; } = WorkerState.Idle;
    internal bool IsLoadingUi { get; set; }
    // Launch automation (account sync, first-time Auto-Tune, market scan) runs only in the real app, not in tests.
    private static bool s_operatorSettingsExistedAtLaunch = true;
    internal bool LaunchAutomationEnabled { get; private set; }
    internal bool IsFirstRun { get; private set; }

    public MainWindow()
        : this(
            new DesktopWorkerSupervisor(),
            () => TradingLimitsFile.LoadOrDefault(LimitsPath),
            limits => TradingLimitsFile.Save(LimitsPath, limits),
            () => Gw2ClientSettingsFile.LoadOrDefault(ClientSettingsPath),
            settings => Gw2ClientSettingsFile.Save(ClientSettingsPath, settings),
            new Gw2ClientPreparer(),
            Gw2InstallationLocator.DetectDefaultExecutablePath,
            () =>
            {
                // Checked before anything saves, so the very first launch can Auto-Tune once.
                s_operatorSettingsExistedAtLaunch = File.Exists(OperatorSettingsPath);
                return OperatorSettingsFile.LoadOrDefault(OperatorSettingsPath);
            },
            settings => OperatorSettingsFile.Save(OperatorSettingsPath, settings),
            marketScanner: null,
            loadCustomList: () => OperatorListsStore.LoadShoppingList(CustomShoppingListPath),
            loadBlacklist: () => OperatorListsStore.LoadBlacklist(BlacklistStorePath),
            ledgerStore: new TradingLedgerStore(LedgerDbPath),
            detectDisplayScale: WindowsDisplayHealth.GetPrimaryDisplayScale,
            discoverGw2Installs: Gw2InstallationLocator.DiscoverInstallations,
            loadApiKeyVault: () => ApiKeyVaultStore.Load(ApiKeyVaultPath, ApiKeyStorePath),
            saveApiKeyVault: vault =>
            {
                ApiKeyVaultStore.Save(ApiKeyVaultPath, vault);
                if (vault.Entries.Count == 0)
                {
                    WindowsSecretStore.Delete(ApiKeyStorePath);
                }
            },
            sessionReportStore: new SessionReportStore(LedgerDbPath))
    {
        LaunchAutomationEnabled = true;
        IsFirstRun = !s_operatorSettingsExistedAtLaunch;
    }

    internal MainWindow(
        IDesktopWorkerSupervisor supervisor,
        Func<TradingLimits> loadLimits,
        Action<TradingLimits> saveLimits,
        Func<Gw2ClientSettings>? loadClientSettings = null,
        Action<Gw2ClientSettings>? saveClientSettings = null,
        IGw2ClientPreparer? clientPreparer = null,
        Func<string?>? detectGw2Path = null,
        Func<OperatorSettings>? loadOperatorSettings = null,
        Action<OperatorSettings>? saveOperatorSettings = null,
        IGw2MarketScanner? marketScanner = null,
        Func<IReadOnlyList<ItemCandidate>>? loadCustomList = null,
        Func<IReadOnlyList<string>>? loadBlacklist = null,
        TradingLedgerStore? ledgerStore = null,
        Func<DisplayScaleInfo>? detectDisplayScale = null,
        Func<IReadOnlyList<Gw2InstallCandidate>>? discoverGw2Installs = null,
        Func<Gw2ApiKeyVaultData>? loadApiKeyVault = null,
        Action<Gw2ApiKeyVaultData>? saveApiKeyVault = null,
        SessionReportStore? sessionReportStore = null,
        Action? requestAppClose = null,
        Action<int>? executeSystemShutdown = null)
    {
        Supervisor = supervisor;
        ReadSavedLimits = loadLimits;
        SaveLimits = saveLimits;
        ReadSavedClientSettings = loadClientSettings ?? (() => new Gw2ClientSettings());
        SaveClientSettings = saveClientSettings ?? (_ => { });
        ReadSavedOperatorSettings = loadOperatorSettings ?? (() => new OperatorSettings());
        SaveOperatorSettings = saveOperatorSettings ?? (_ => { });
        ClientPreparer = clientPreparer ?? new Gw2ClientPreparer();
        DetectGw2Path = detectGw2Path ?? (() => null);
        DiscoverGw2Installs = discoverGw2Installs ?? (() => []);
        DetectDisplayScale = detectDisplayScale ?? (() => new DisplayScaleInfo(96, 100, true, "100% (96 DPI)"));
        LedgerStore = ledgerStore;
        SessionReportStore = sessionReportStore;
        MarketScanner = marketScanner ?? new Gw2MarketScanner(Gw2HttpClient, LedgerStore);
        _loadApiKeyVault = loadApiKeyVault ?? (() => new Gw2ApiKeyVaultData(null, Array.Empty<Gw2ApiKeyEntry>()));
        SaveApiKeyVault = saveApiKeyVault ?? (_ => { });
        _requestAppClose = requestAppClose ?? Close;
        _executeSystemShutdown = executeSystemShutdown ?? TriggerWindowsSystemShutdown;

        IsLoadingUi = true;
        _cycleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _cycleTimer.Tick += (_, _) => UpdateMarketCycleDisplay();
        _sessionClockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _sessionClockTimer.Tick += (_, _) => SessionPage.UpdateSessionClockDisplay();

        InitializeComponent();
        SessionPage.Attach(this);
        GuidePage.Attach(this);
        ListsPage.Attach(this);
        ReportsPage.Attach(this);
        ScannerPage.Attach(this);
        SettingsPage.Attach(this);
        IncomePage.Attach(this);

        SettingsPage.LoadLimits();
        SettingsPage.LoadClientSettings();
        SettingsPage.LoadOperatorSettings();
        ListsPage.LoadLists(
            loadCustomList ?? (() => Array.Empty<ItemCandidate>()),
            loadBlacklist ?? (() => Array.Empty<string>()));
        LoadApiKeyVault();
        UpdateMarketCycleDisplay();
        SessionPage.UpdateSessionPaceSummary();
        RefreshReadinessChecklist();
        UpdateSessionButtonStates(WorkerState.Idle);
        SessionPage.UpdateSafeModePill();
        UpdateSidebarStatus(WorkerState.Idle, "Ready (Offline)");
        IsLoadingUi = false;

        _cycleTimer.Start();

        Supervisor.EventReceived += Supervisor_EventReceived;
        SourceInitialized += (_, _) =>
        {
            if (Application.Current is App app)
            {
                app.ApplyWindowTitleBar(this);
            }

            _hotkeys.Attach(this);
            RegisterConfiguredHotkeys(logWarnings: false);
        };
        Loaded += async (_, _) =>
        {
            await IncomePage.LoadCachedPortfolioOnStartupAsync();
            await ReportsPage.LoadSessionReportsAsync();
            await SessionPage.RunLaunchAutomationAsync();
        };
        Closed += (_, _) =>
        {
            _cycleTimer.Stop();
            _sessionClockTimer.Stop();
            ScannerPage.CancelRunningScan();
            _hotkeys.Dispose();
            SettingsPage.SaveAllSettingsSilently();
            Gw2HttpClient.Dispose();
            var disposeTask = Task.Run(async () => await Supervisor.DisposeAsync().ConfigureAwait(false));
            disposeTask.Wait(TimeSpan.FromSeconds(2));
        };
    }

    // ---------------------------------------------------------------------
    // Navigation & Theme
    // ---------------------------------------------------------------------

    /// <summary>Adds a line to the Session page's activity log.</summary>
    internal void AddLog(string message, string? rawMessage = null) => SessionPage.AddLog(message, rawMessage);

    /// <summary>The pages in the content area. Each page's XAML has its own namescope.</summary>
    private IEnumerable<FrameworkElement> Pages => [SessionPage, GuidePage, ListsPage, ReportsPage, ScannerPage, IncomePage, SettingsPage];

    /// <summary>Finds a named element in the shell or in any page.</summary>
    internal object? FindElement(string name) =>
        FindName(name) ?? Pages.Select(page => page.FindName(name)).FirstOrDefault(element => element is not null);

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        // The first nav button is checked while the window XAML loads, before the pages exist.
        if (SessionPage is null || IncomePage is null || ReportsPage is null || ScannerPage is null ||
            ListsPage is null || SettingsPage is null || GuidePage is null)
        {
            return;
        }

        SessionPage.SessionView.Visibility = VisibleWhenChecked(NavSession);
        IncomePage.IncomeView.Visibility = VisibleWhenChecked(NavIncome);
        ReportsPage.ReportsView.Visibility = VisibleWhenChecked(NavReports);
        ScannerPage.ScannerView.Visibility = VisibleWhenChecked(NavScanner);
        ListsPage.ListsView.Visibility = VisibleWhenChecked(NavLists);
        SettingsPage.SettingsView.Visibility = VisibleWhenChecked(NavSettings);
        GuidePage.GuideView.Visibility = VisibleWhenChecked(NavGuide);

        if (NavReports.IsChecked == true)
        {
            _ = ReportsPage.LoadSessionReportsAsync();
        }
    }

    private static Visibility VisibleWhenChecked(RadioButton navButton) =>
        navButton.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    private void ThemeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoadingUi || ThemeSelector.SelectedItem is not ComboBoxItem item) return;
        var mode = item.Content.ToString()!;
        if (Application.Current is App app)
        {
            app.ApplyTheme(mode);
            app.ApplyWindowTitleBar(this);
        }

        IncomePage.RefreshAfterThemeChange();
        ReportsPage.RefreshAfterThemeChange();

        RefreshReadinessChecklist();
        SettingsPage.SaveOperatorSettingsIfValid();
    }

    // ---------------------------------------------------------------------
    // Session state & side-by-side docking
    // ---------------------------------------------------------------------

    internal void UpdateSessionButtonStates(WorkerState state)
    {
        var isPaused = state == WorkerState.Paused;
        var isActive = state is WorkerState.Starting
            or WorkerState.LaunchingClient
            or WorkerState.WaitingForLogin
            or WorkerState.TradingPostReady
            or WorkerState.ProcessingTrading
            or WorkerState.Recovering
            or WorkerState.Fishing;

        var isEnded = state is WorkerState.Stopped or WorkerState.Idle or WorkerState.Faulted or WorkerState.NeedsAttention;
        var inSession = isActive || isPaused;

        SessionPage.StartSessionButton.IsEnabled = !inSession;
        SessionPage.StartSessionButton.Content = StartButtonLabel(isActive, isPaused);
        SessionPage.PauseSessionButton.IsEnabled = isActive;
        SessionPage.ResumeSessionButton.IsEnabled = isPaused;
        SessionPage.StopSessionButton.IsEnabled = inSession;
        SessionPage.EmergencyStopButton.IsEnabled = inSession;
        SessionPage.TryOfflineButton.IsEnabled = !inSession;
        SessionPage.SwapCharacterNowButton.IsEnabled = !inSession;

        SessionPage.BotSelector.IsEnabled = !inSession;
        SessionPage.ModeSelector.IsEnabled = !inSession;
        SessionPage.ProfileSelector.IsEnabled = !inSession;
        SessionPage.SourceSelector.IsEnabled = !inSession;
        SessionPage.ArmLiveInputCheckBox.IsEnabled = !inSession;
        SessionPage.AutoSellInventoryCheckBox.IsEnabled = !inSession;

        if (isActive && SettingsPage.AutoDockBesideGw2CheckBox.IsChecked == true)
        {
            ApplySideDockBesideGw2();
        }
        else if (isEnded)
        {
            RestorePreDockBounds();
        }

        if (isActive)
        {
            StartSessionClock();
        }
        else if (isEnded)
        {
            _sessionClockTimer.Stop();
        }
        else if (!isPaused)
        {
            return;
        }

        SessionPage.UpdateSessionClockDisplay();
    }

    private static string StartButtonLabel(bool isActive, bool isPaused)
    {
        if (isActive)
        {
            return "Running...";
        }

        return isPaused ? "Paused" : "Start session";
    }

    private void StartSessionClock()
    {
        if (SessionStartedAtUtc is null)
        {
            SessionStartedAtUtc = DateTimeOffset.UtcNow;
            if (Gw2ClientSettings.TryParseMaxSessionHours(SettingsPage.MaxSessionHoursBox.Text, out var maxMins, out _) && maxMins is > 0)
            {
                SessionExpiresAtUtc = SessionStartedAtUtc.Value.AddMinutes(maxMins.Value);
            }
        }

        if (!_sessionClockTimer.IsEnabled)
        {
            _sessionClockTimer.Start();
        }
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ApplyResponsiveLayout(e.NewSize.Width);

    internal void ToggleSideDock()
    {
        if (_isSideDocked)
        {
            RestorePreDockBounds();
        }
        else
        {
            ApplySideDockBesideGw2();
        }
    }

    internal void ApplyResponsiveLayout(double width)
    {
        if (SidebarColumn is null || SidebarBorder is null || MainContentBorder is null ||
            SidebarStatusBorder is null || SidebarStatusRow is null || SidebarStatusDot is null ||
            SessionPage is null || IncomePage is null || ReportsPage is null || ScannerPage is null ||
            ListsPage is null || GuidePage is null)
        {
            return;
        }

        var compact = width > 0 && width < 940;
        var mediumCompact = width > 0 && width < 960;
        var narrowCharts = width > 0 && width < 760;
        var labelVisibility = compact ? Visibility.Collapsed : Visibility.Visible;

        SidebarColumn.Width = new GridLength(compact ? 64 : 228);
        SidebarBorder.Padding = compact ? new Thickness(9, 16, 9, 16) : new Thickness(16, 20, 16, 20);
        MainContentBorder.Padding = compact ? new Thickness(14, 12, 14, 12) : new Thickness(24, 20, 24, 20);

        if (SidebarBrandRow is not null && SidebarBrandIconBorder is not null)
        {
            SidebarBrandRow.HorizontalAlignment = compact ? HorizontalAlignment.Center : HorizontalAlignment.Left;
            SidebarBrandIconBorder.Margin = compact ? new Thickness(0) : new Thickness(0, 0, 11, 0);
        }

        if (compact)
        {
            SidebarStatusBorder.Width = 40;
            SidebarStatusBorder.Height = 40;
            SidebarStatusBorder.Padding = new Thickness(0);
            SidebarStatusBorder.CornerRadius = new CornerRadius(10);
            SidebarStatusBorder.HorizontalAlignment = HorizontalAlignment.Center;
            SidebarStatusRow.HorizontalAlignment = HorizontalAlignment.Center;
            SidebarStatusDot.Width = 11;
            SidebarStatusDot.Height = 11;
            SidebarStatusDot.Margin = new Thickness(0);
            // Center the dot in the 40x40 tile; the stack otherwise sits at the top and the rounded corner clips it.
            SidebarStatusStack.HorizontalAlignment = HorizontalAlignment.Center;
            SidebarStatusStack.VerticalAlignment = VerticalAlignment.Center;
        }
        else
        {
            SidebarStatusBorder.Width = double.NaN;
            SidebarStatusBorder.Height = double.NaN;
            SidebarStatusBorder.Padding = new Thickness(12, 10, 12, 10);
            SidebarStatusBorder.CornerRadius = new CornerRadius(9);
            SidebarStatusBorder.HorizontalAlignment = HorizontalAlignment.Stretch;
            SidebarStatusRow.HorizontalAlignment = HorizontalAlignment.Left;
            SidebarStatusDot.Width = 9;
            SidebarStatusDot.Height = 9;
            SidebarStatusDot.Margin = new Thickness(0, 0, 8, 0);
            SidebarStatusStack.HorizontalAlignment = HorizontalAlignment.Stretch;
            SidebarStatusStack.VerticalAlignment = VerticalAlignment.Stretch;
        }

        // Docked: pills go under the title so "Session Control Room" is not squeezed to "Session...".
        PlaceHeaderActions(SessionPage.SessionHeaderPills, compact, compactTopMargin: 8);

        SidebarBrandText.Visibility = labelVisibility;
        SidebarFooterExpanded.Visibility = labelVisibility;
        SidebarHotkeyHint.Visibility = labelVisibility;
        SidebarStatusLabel.Visibility = labelVisibility;
        SidebarWorkspaceLabel.Visibility = labelVisibility;
        NavSessionLabel.Visibility = labelVisibility;
        NavIncomeLabel.Visibility = labelVisibility;
        NavReportsLabel.Visibility = labelVisibility;
        NavScannerLabel.Visibility = labelVisibility;
        NavListsLabel.Visibility = labelVisibility;
        NavSettingsLabel.Visibility = labelVisibility;
        NavGuideLabel.Visibility = labelVisibility;

        var columns = compact ? 2 : 4;
        // Selectors and KPI cards stay in one row down to 760 px (docked beside GW2 is ~870 px), so the
        // Activity Log and the Income tables keep most of the height.
        var cardColumns = narrowCharts ? 2 : 4;
        SessionPage.SessionSelectorsGrid.Columns = cardColumns;
        SessionPage.SessionKpiGrid.Columns = cardColumns;
        IncomePage.IncomeKpiGrid.Columns = cardColumns;
        SessionPage.SessionSubtitleText.Visibility = labelVisibility;
        IncomePage.IncomeSubtitleText.Visibility = labelVisibility;
        GuidePage.GuideScheduleGrid.Columns = cardColumns;
        ScannerPage.ScannerCoreFiltersGrid.Columns = columns;
        ScannerPage.ScannerAdvancedFiltersGrid.Columns = columns;
        ReportsPage.ReportDetailKpiGrid.Columns = columns;

        PlaceHeaderActions(IncomePage.IncomeHeaderActionsPanel, compact, compactTopMargin: 10);

        // Narrow windows show one chart (its selector also offers "Activity by hour") so the tables keep room.
        IncomePage.IncomeChartsLeftCol.Width = new GridLength(compact ? 1 : 1.2, GridUnitType.Star);
        IncomePage.IncomeChartsRightCol.Width = compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        SetGridCell(IncomePage.DailyProfitChartCard, row: 0, column: 0);
        IncomePage.DailyProfitChartCard.Margin = compact ? new Thickness(0) : new Thickness(0, 0, 12, 0);
        if (!compact)
        {
            SetGridCell(IncomePage.HourlyActivityChartCard, row: 0, column: 1);
        }

        IncomePage.HourlyActivityChartCard.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;

        ApplyTwoPaneLayout(
            stacked: mediumCompact,
            ListsPage.ListsLeftCol, ListsPage.ListsRightCol, ListsPage.ListsTopRow, ListsPage.ListsBottomRow,
            ListsPage.CustomListCard, ListsPage.BlacklistCard,
            stackedTopHeight: new GridLength(1, GridUnitType.Star),
            sideBySideLeftWidth: new GridLength(1.2, GridUnitType.Star));

        ApplyTwoPaneLayout(
            stacked: compact,
            ReportsPage.ReportsLeftCol, ReportsPage.ReportsRightCol, ReportsPage.ReportsTopRow, ReportsPage.ReportsBottomRow,
            ReportsPage.ReportsListCard, ReportsPage.ReportDetailCard,
            stackedTopHeight: new GridLength(200),
            sideBySideLeftWidth: new GridLength(300));
    }

    private static void SetGridCell(UIElement element, int row, int column)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
    }

    /// <summary>Compact: header actions wrap under the page title; otherwise they sit right of it.</summary>
    private static void PlaceHeaderActions(FrameworkElement actions, bool compact, double compactTopMargin)
    {
        SetGridCell(actions, row: compact ? 1 : 0, column: compact ? 0 : 1);
        Grid.SetColumnSpan(actions, compact ? 2 : 1);
        actions.HorizontalAlignment = compact ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        actions.Margin = compact ? new Thickness(0, compactTopMargin, 0, 0) : new Thickness(0);
    }

    /// <summary>Stacks two cards vertically, or places them side by side, in a 2x2 page grid.</summary>
    private static void ApplyTwoPaneLayout(
        bool stacked,
        ColumnDefinition leftColumn,
        ColumnDefinition rightColumn,
        RowDefinition topRow,
        RowDefinition bottomRow,
        FrameworkElement firstCard,
        FrameworkElement secondCard,
        GridLength stackedTopHeight,
        GridLength sideBySideLeftWidth)
    {
        SetGridCell(firstCard, row: 0, column: 0);
        if (stacked)
        {
            leftColumn.Width = new GridLength(1, GridUnitType.Star);
            rightColumn.Width = new GridLength(0);
            topRow.Height = stackedTopHeight;
            bottomRow.Height = new GridLength(1, GridUnitType.Star);
            firstCard.Margin = new Thickness(0, 0, 0, 12);
            SetGridCell(secondCard, row: 1, column: 0);
        }
        else
        {
            leftColumn.Width = sideBySideLeftWidth;
            rightColumn.Width = new GridLength(1, GridUnitType.Star);
            topRow.Height = new GridLength(1, GridUnitType.Star);
            bottomRow.Height = GridLength.Auto;
            firstCard.Margin = new Thickness(0, 0, 14, 0);
            SetGridCell(secondCard, row: 0, column: 1);
        }
    }

    internal bool ApplySideDockBesideGw2()
    {
        if (!IsVisible)
        {
            return false;
        }

        var gw2Width = int.TryParse(SettingsPage.Gw2WidthBox.Text.Trim(), out var parsedW) && parsedW >= 640 ? parsedW : 1024;
        var workArea = SystemParameters.WorkArea;
        var desiredLeft = workArea.Left + gw2Width + 6;
        var availableWidth = workArea.Right - desiredLeft;
        if (availableWidth < 520)
        {
            return false;
        }

        if (!_isSideDocked)
        {
            _preDockWindowState = WindowState;
            _preDockBounds = WindowState == WindowState.Maximized
                ? RestoreBounds
                : new Rect(Left, Top, Width, Height);
        }

        if (WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Normal;
        }

        Left = desiredLeft;
        Top = workArea.Top + 4;
        Width = Math.Clamp(availableWidth - 4, 520, 920);
        Height = Math.Max(560, workArea.Height - 8);
        _isSideDocked = true;
        SessionPage.DockToggleButton.Content = "Restore size";
        SessionPage.DockToggleButton.ToolTip = "Restore size: puts Mmopal back to the window size it had before docking.";
        ApplyResponsiveLayout(Width);
        return true;
    }

    internal void RestorePreDockBounds()
    {
        if (!_isSideDocked || _preDockBounds is not { } saved)
        {
            return;
        }

        _isSideDocked = false;
        Left = saved.Left;
        Top = saved.Top;
        Width = Math.Max(MinWidth, saved.Width);
        Height = Math.Max(MinHeight, saved.Height);
        if (_preDockWindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Maximized;
        }

        _preDockBounds = null;
        SessionPage.DockToggleButton.Content = "Dock beside GW2";
        SessionPage.DockToggleButton.ToolTip = "Dock beside GW2: fits Mmopal next to the 1024x768 game window, or restores your size.";
        ApplyResponsiveLayout(Width);
    }

    // The smoke test drives Auto-Tune through the window.
    internal void ApplyStrategyPresetToSettings(TradingStrategyPreset preset) => SettingsPage.ApplyStrategyPresetToSettings(preset);

    // ---------------------------------------------------------------------
    // Account switching (sidebar)
    // ---------------------------------------------------------------------

    private async void SidebarAccountSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoadingUi || SidebarAccountSelector.SelectedItem is not ComboBoxItem { Tag: string keyId })
        {
            return;
        }

        await SwitchActiveAccountAsync(keyId);
    }

    // ---------------------------------------------------------------------
    // Worker Communication & Event Handling
    // ---------------------------------------------------------------------

    internal async Task SendAsync(WorkerCommand command)
    {
        try
        {
            var response = await Supervisor.SendAsync(command);
            ApplyEvent(response);
        }
        catch (Exception exception)
        {
            AddLog($"ERROR: {exception.Message}");
            SessionPage.StatusText.Text = "Worker unavailable";
            UpdateSidebarStatus(WorkerState.Faulted, "Worker unavailable");
        }
    }

    private void Supervisor_EventReceived(object? sender, WorkerEvent workerEvent)
    {
        _ = Dispatcher.InvokeAsync(() => ApplyEvent(workerEvent));
    }

    private void ApplyEvent(WorkerEvent workerEvent)
    {
        var rawLog = $"{workerEvent.Type}: {workerEvent.Payload}";
        if (workerEvent.Type == WorkerEventTypes.Status)
        {
            ApplyStatusEvent(workerEvent.Payload, rawLog);
        }
        else if (workerEvent.Type == WorkerEventTypes.Health)
        {
            ApplyHealthEvent(workerEvent.Payload, rawLog);
        }
        else if (workerEvent.Type == WorkerEventTypes.SessionReport)
        {
            ApplySessionReportEvent(workerEvent.Payload, rawLog);
        }
        else
        {
            AddLog(rawLog, rawLog);
        }
    }

    private void ApplyStatusEvent(string payload, string rawLog)
    {
        var status = JsonSerializer.Deserialize<WorkerStatus>(payload);
        if (status is null)
        {
            SessionPage.StatusText.Text = payload;
            AddLog(rawLog, rawLog);
            return;
        }

        var statusDetail = status.Message ?? status.LastAction;
        LastWorkerState = status.State;
        SessionPage.StatusText.Text = statusDetail ?? string.Empty;
        var stateLabel = SessionPage.UpdateStatusCard(status.State, statusDetail);
        UpdateSidebarStatus(status.State, stateLabel);
        UpdateSessionButtonStates(status.State);
        AddLog(
            string.IsNullOrWhiteSpace(statusDetail)
                ? $"Session state: {status.State}"
                : $"Status ({status.State}): {statusDetail}",
            rawLog);
    }

    private void ApplyHealthEvent(string payload, string rawLog)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        var src = root.TryGetProperty("source", out var source) ? source.GetString() ?? "-" : "-";
        SessionPage.ObservationSourceText.Text = src switch
        {
            "screen" => "Screen observe",
            "replay" => "replay",
            _ => src
        };

        bool? tpOpen = null;
        if (root.TryGetProperty("ui", out var ui) && ui.ValueKind == JsonValueKind.Object)
        {
            var open = ui.TryGetProperty("tradingPostOpen", out var tradingPostOpen) && tradingPostOpen.GetBoolean();
            var item = ui.TryGetProperty("itemWindowOpen", out var itemWindowOpen) && itemWindowOpen.GetBoolean();
            tpOpen = open;
            SessionPage.ObservationUiText.Text = $"Trading Post: {(open ? "open" : "closed")}; item: {(item ? "open" : "closed")}";
            SessionPage.GameHeadlineText.Text = open ? "TP open" : "TP closed";
            SessionPage.GameHeadlineText.Foreground = open
                ? ResolveBrush("SuccessBrush", Brushes.SeaGreen)
                : ResolveBrush("WarningBrush", Brushes.DarkOrange);
            SessionPage.GameCard.ToolTip = SessionPage.ObservationUiText.Text;
        }

        SessionPage.ObservationPriceText.Text =
            root.TryGetProperty("visiblePrice", out var visiblePrice) &&
            visiblePrice.ValueKind == JsonValueKind.Object &&
            visiblePrice.TryGetProperty("price", out var price) &&
            price.ValueKind == JsonValueKind.Object &&
            price.TryGetProperty("copper", out var copper)
                ? copper.ToString()
                : "-";

        var timingSummary = root.TryGetProperty("timings", out var timings) ? FormatTimings(timings) : "-";
        SessionPage.ObservationTimingText.Text = timingSummary;
        SessionPage.ObservationTimingText.ToolTip = "Last cycle: GW2 API round trip and screen read time.";

        var buyPausedLowGold = root.TryGetProperty("buyPausedLowGold", out var lowGoldProp) &&
                               lowGoldProp.ValueKind == JsonValueKind.True;
        var orderBookText = GetStringProperty(root, "orderBook");
        if (buyPausedLowGold)
        {
            SessionPage.ObservationOrdersText.Text = !string.IsNullOrWhiteSpace(orderBookText)
                ? $"Buys paused (0g/reserve floor) • {orderBookText}"
                : "Buys paused (0g/reserve floor) — sell/collect only";
        }
        else if (orderBookText is not null)
        {
            SessionPage.ObservationOrdersText.Text = orderBookText;
        }

        SessionPage.UpdateOrdersCard(SessionPage.ObservationOrdersText.Text, buyPausedLowGold);

        if (TryGetStringProperty(root, "characterSwap", out var swap))
        {
            SessionPage.ShowSwapStatus(swap ?? "AFK swap disabled");
        }

        if (TryGetStringProperty(root, "sessionCap", out var sessionCap))
        {
            SessionPage.SessionCapStatusText.Text = sessionCap ?? "Max session cap disabled";
        }

        if (TryGetStringProperty(root, "marketList", out var marketList))
        {
            SessionPage.MarketListStatusText.Text = $"Shopping list: {marketList}";
        }

        if (TryGetStringProperty(root, "portfolio", out var portfolio))
        {
            SessionPage.PortfolioStatusText.Text = portfolio ?? "History: —";
        }

        ApplySessionTimes(root);
        SessionPage.UpdateSessionClockDisplay();

        var summaryParts = new List<string>
        {
            $"Cycle [{src}]",
            $"{GetInt32Property(root, "decisions")} evaluated",
            $"{GetInt32Property(root, "actions")} actions"
        };
        if (tpOpen.HasValue) summaryParts.Insert(1, tpOpen.Value ? "TP open" : "TP closed");
        var soldCount = GetInt32Property(root, "soldCount");
        if (soldCount > 0) summaryParts.Add($"{soldCount} listed sell");
        var canceledOutbid = GetInt32Property(root, "canceledOutbidBuys");
        if (canceledOutbid > 0) summaryParts.Add($"{canceledOutbid} outbid buys refreshed");
        if (buyPausedLowGold) summaryParts.Add("buys paused (0g/floor)");
        if (!string.IsNullOrWhiteSpace(timingSummary) && timingSummary != "-") summaryParts.Add(timingSummary);
        var healthError = GetStringProperty(root, "error");
        if (!string.IsNullOrWhiteSpace(healthError)) summaryParts.Add($"error: {healthError}");

        AddLog(string.Join(" • ", summaryParts), rawLog);
    }

    /// <summary>The worker owns the session clock; its start and cap times override the local guesses.</summary>
    private void ApplySessionTimes(JsonElement root)
    {
        if (TryGetStringProperty(root, "sessionStartedAt", out var startedText) &&
            DateTimeOffset.TryParse(startedText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var startedAt))
        {
            SessionStartedAtUtc = startedAt;
        }

        if (!root.TryGetProperty("sessionExpiresAt", out var expiresProp))
        {
            return;
        }

        if (expiresProp.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(expiresProp.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var expiresAt))
        {
            SessionExpiresAtUtc = expiresAt;
        }
        else if (expiresProp.ValueKind == JsonValueKind.Null)
        {
            SessionExpiresAtUtc = null;
        }
    }

    private void ApplySessionReportEvent(string payload, string rawLog)
    {
        SessionReportNotification? notification = null;
        try
        {
            notification = JsonSerializer.Deserialize<SessionReportNotification>(payload, PayloadJson);
        }
        catch (JsonException)
        {
            // Fall back to raw logging below if payload shape was unexpected.
        }

        if (notification is null)
        {
            AddLog(rawLog, rawLog);
            return;
        }

        var report = notification.Report;
        var duration = SessionReportFormatter.FormatDuration(report.DurationSeconds);
        var message = notification.Saved
            ? $"Session report saved [{report.Outcome}] — {report.TotalTradeActions} trade action(s), {report.DeliveryCollections} pickup(s), {report.CharacterSwapsCompleted} swap(s), duration {duration}."
            : $"Session finished [{report.Outcome}] in {duration} (not saved per report eligibility rules).";
        AddLog(message, rawLog);
        _ = ReportsPage.LoadSessionReportsAsync();
        if (notification.CapReached && (notification.ShutdownPc || notification.AutoCloseApp))
        {
            HandleSessionCapCompletion(notification);
        }
    }

    private static bool TryGetStringProperty(JsonElement element, string name, out string? value)
    {
        if (element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString();
            return true;
        }

        value = null;
        return false;
    }

    private static string? GetStringProperty(JsonElement element, string name) =>
        TryGetStringProperty(element, name, out var value) ? value : null;

    private static int GetInt32Property(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number
            ? property.GetInt32()
            : 0;

    private static string FormatTimings(JsonElement timings)
    {
        if (timings.ValueKind != JsonValueKind.Object)
        {
            return timings.ToString();
        }

        var parts = new List<string>();
        if (timings.TryGetProperty("api", out var apiMs)) parts.Add($"api {FormatMs(apiMs)}");
        if (timings.TryGetProperty("ui", out var uiMs)) parts.Add($"ui {FormatMs(uiMs)}");
        return parts.Count > 0 ? string.Join(" · ", parts) : timings.ToString();
    }

    // ---------------------------------------------------------------------
    // Market Cycle, Readiness Checklist & Persistence Helpers
    // ---------------------------------------------------------------------

    private void UpdateMarketCycleDisplay()
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var info = Gw2MarketCycle.Describe(nowUtc);
        // Compact pill; the full schedule line and advice live in the tooltip.
        SessionPage.MarketCyclePillText.Text = $"{ShortPhaseName(info.PhaseName)} · reset {(int)info.TimeUntilReset.TotalHours}h {info.TimeUntilReset.Minutes:00}m";
        if (SessionPage.MarketCyclePillBorder is not null)
        {
            SessionPage.MarketCyclePillBorder.ToolTip = $"{info.Summary}\n{info.Advice}";
        }

        GuidePage.UpdateSchedule(info, nowUtc);
    }

    private static string ShortPhaseName(string phase) => phase switch
    {
        "Global Off-Peak" => "Off-peak",
        "EU Prime Time" => "EU prime",
        "NA Prime Time" => "NA prime",
        "Daily Reset Spike" => "Reset spike",
        _ => phase
    };

    private static string FormatMs(JsonElement value)
    {
        if (!value.TryGetDouble(out var ms))
        {
            return value.ToString();
        }

        return ms >= 1000
            ? (ms / 1000).ToString("0.0", CultureInfo.InvariantCulture) + "s"
            : ms.ToString("0", CultureInfo.InvariantCulture) + "ms";
    }

    internal void RefreshReadinessChecklist()
    {
        var readyCount = GuidePage.RefreshChecklist();
        if (ReadinessBannerDismissed || readyCount == 4)
        {
            SessionPage.ReadinessBanner.Visibility = Visibility.Collapsed;
        }
        else
        {
            SessionPage.ReadinessBanner.Visibility = Visibility.Visible;
            SessionPage.ReadinessBannerText.Text = $"Setup readiness: {readyCount}/4 checks complete — open Guide & Setup for quick one-click fixes and flipping tips.";
        }
    }

    private int GetConfiguredApiTimeoutSeconds() =>
        int.TryParse(SettingsPage.ApiTimeoutSecondsBox.Text.Trim(), out var parsed)
            ? Math.Clamp(parsed, 15, 120)
            : 60;

    internal CancellationTokenSource CreateApiCancellationTokenSource() =>
        new(TimeSpan.FromSeconds(GetConfiguredApiTimeoutSeconds()));

    internal string? ResolveConfiguredApiKey() =>
        ApiKeyVault.ActiveEntry?.ApiKey?.Trim() is { Length: > 0 } vaultKey
            ? vaultKey
            : Environment.GetEnvironmentVariable("MMOPAL_Native__Gw2ApiKey");

    internal string? ResolveActiveSqlAccountName()
    {
        var name = ApiKeyVault.ActiveEntry?.AccountName?.Trim();
        if (string.IsNullOrWhiteSpace(name) || string.Equals(name, "Default Account", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return name;
    }

    private void LoadApiKeyVault()
    {
        ApiKeyVault = _loadApiKeyVault();
        RefreshHasVerifiedApiKey();
        SelectActiveAccountLaunchPlatform();
        RebuildApiKeyViews();
    }

    internal void RefreshHasVerifiedApiKey() =>
        HasVerifiedApiKey = ApiKeyVault.ActiveEntry?.Status.Equals("VALID", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Shows the active account's launch platform without saving settings back.</summary>
    private void SelectActiveAccountLaunchPlatform()
    {
        if (string.IsNullOrWhiteSpace(ApiKeyVault.ActiveEntry?.LaunchPlatform))
        {
            return;
        }

        var wasLoading = IsLoadingUi;
        IsLoadingUi = true;
        SelectComboByTag(SettingsPage.LaunchPlatformSelector, ApiKeyVault.ActiveEntry.LaunchPlatform);
        IsLoadingUi = wasLoading;
    }

    /// <summary>An idle worker is restarted so it picks up the newly active account.</summary>
    internal async Task RestartWorkerIfIdleAsync()
    {
        if (Supervisor is DesktopWorkerSupervisor concreteSupervisor && LastWorkerState is WorkerState.Idle or WorkerState.Stopped)
        {
            await concreteSupervisor.RestartWorkerAsync();
        }
    }

    internal void RebuildApiKeyViews()
    {
        SettingsPage.RebuildApiKeyRows();
        var activeId = ApiKeyVault.ActiveEntry?.Id;
        var wasLoading = IsLoadingUi;
        IsLoadingUi = true;
        try
        {
            SidebarAccountSelector.Items.Clear();
            if (ApiKeyVault.Entries.Count == 0)
            {
                SidebarAccountSelector.Items.Add(new ComboBoxItem { Content = "No account added" });
                SidebarAccountSelector.SelectedIndex = 0;
                SidebarAccountSelector.IsEnabled = false;
            }
            else
            {
                SidebarAccountSelector.IsEnabled = true;
                var selectedIndex = 0;
                for (var i = 0; i < ApiKeyVault.Entries.Count; i++)
                {
                    var entry = ApiKeyVault.Entries[i];
                    SidebarAccountSelector.Items.Add(new ComboBoxItem { Content = entry.DisplayLabel, Tag = entry.Id });
                    if (string.Equals(entry.Id, activeId, StringComparison.OrdinalIgnoreCase))
                    {
                        selectedIndex = i;
                    }
                }

                SidebarAccountSelector.SelectedIndex = selectedIndex;
            }
        }
        finally
        {
            IsLoadingUi = wasLoading;
        }

        IncomePage.IncomeActiveAccountText.Text = ApiKeyVault.ActiveEntry is { } active
            ? $"Account: {active.DisplayLabel}"
            : "No active account";

        SettingsPage.RefreshApiKeyIndicator();
        SessionPage.UpdateSessionPaceSummary();
        UpdateSidebarStatusTooltip();
    }

    internal async Task SwitchActiveAccountAsync(string keyId)
    {
        if (string.Equals(ApiKeyVault.ActiveKeyId, keyId, StringComparison.OrdinalIgnoreCase) ||
            !ApiKeyVault.Entries.Any(e => string.Equals(e.Id, keyId, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        ApiKeyVault = ApiKeyVault with { ActiveKeyId = keyId };
        SaveApiKeyVault(ApiKeyVault);
        RefreshHasVerifiedApiKey();
        SelectActiveAccountLaunchPlatform();
        RebuildApiKeyViews();
        RefreshReadinessChecklist();
        SettingsPage.SaveAllSettingsSilently();

        var activeLabel = ApiKeyVault.ActiveEntry?.DisplayLabel ?? "Account";
        AddLog($"Switched active account to {activeLabel}.");
        await IncomePage.ReloadLedgerForActiveAccountAsync();
        await ReportsPage.LoadSessionReportsAsync();
        await RestartWorkerIfIdleAsync();
    }

    internal void UpdateVaultEntry(string keyId, Func<Gw2ApiKeyEntry, Gw2ApiKeyEntry> transform)
    {
        var updated = ApiKeyVault.Entries
            .Select(item => string.Equals(item.Id, keyId, StringComparison.OrdinalIgnoreCase) ? transform(item) : item)
            .ToArray();
        ApiKeyVault = ApiKeyVault with { Entries = updated };
        SaveApiKeyVault(ApiKeyVault);
    }

    internal async Task RemoveVaultEntryAsync(string keyId)
    {
        var remaining = ApiKeyVault.Entries
            .Where(item => !string.Equals(item.Id, keyId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var nextActiveId = string.Equals(ApiKeyVault.ActiveKeyId, keyId, StringComparison.OrdinalIgnoreCase)
            ? remaining.FirstOrDefault()?.Id
            : ApiKeyVault.ActiveKeyId;

        ApiKeyVault = new Gw2ApiKeyVaultData(nextActiveId, remaining);
        SaveApiKeyVault(ApiKeyVault);
        RefreshHasVerifiedApiKey();
        RebuildApiKeyViews();
        RefreshReadinessChecklist();
        AddLog("Removed API key from DPAPI vault.");

        await IncomePage.ReloadLedgerForActiveAccountAsync();
        await ReportsPage.LoadSessionReportsAsync();
        await RestartWorkerIfIdleAsync();
    }

    internal void RegisterConfiguredHotkeys(bool logWarnings)
    {
        var pauseKey = NullIfBlank(SettingsPage.PauseHotkeyBox.Text);
        var stopKey = NullIfBlank(SettingsPage.StopHotkeyBox.Text);
        var emergencyKey = NullIfBlank(SettingsPage.EmergencyStopHotkeyBox.Text);
        var warnings = _hotkeys.RegisterSet(
            pauseKey,
            SendOnDispatcher(() => LastWorkerState == WorkerState.Paused ? WorkerCommandTypes.Resume : WorkerCommandTypes.Pause),
            stopKey,
            SendOnDispatcher(() => WorkerCommandTypes.Stop),
            emergencyKey,
            SendOnDispatcher(() => WorkerCommandTypes.EmergencyStop));

        SidebarHotkeyHint.Text = $"Pause: {pauseKey ?? "off"}  •  Stop: {stopKey ?? "off"}";
        if (warnings.Count == 0)
        {
            SettingsPage.HotkeyStatusText.Text = $"Active hotkeys — Pause/Resume: {pauseKey ?? "off"}, Stop: {stopKey ?? "off"}, Emergency: {emergencyKey ?? "off"}.";
        }
        else
        {
            SettingsPage.HotkeyStatusText.Text = string.Join(" ", warnings);
            if (logWarnings)
            {
                foreach (var warning in warnings) AddLog($"Hotkey warning: {warning}");
            }
        }

        UpdateSidebarStatusTooltip();
    }

    /// <summary>Hotkeys fire off the UI thread; the command type is chosen when the key is pressed.</summary>
    private Action SendOnDispatcher(Func<string> commandType) =>
        () => _ = Dispatcher.InvokeAsync(async () => await SendAsync(new WorkerCommand(commandType())));

    private void UpdateSidebarStatus(WorkerState state, string label)
    {
        SidebarStatusLabel.Text = label;
        var brushKey = state switch
        {
            WorkerState.ProcessingTrading or WorkerState.TradingPostReady or WorkerState.Fishing => "SuccessBrush",
            WorkerState.Starting or WorkerState.LaunchingClient or WorkerState.WaitingForLogin => "AccentBrush",
            WorkerState.Paused or WorkerState.Recovering or WorkerState.NeedsAttention => "WarningBrush",
            WorkerState.Faulted => "DangerBrush",
            _ => "TextMutedBrush"
        };
        if (Application.Current?.TryFindResource(brushKey) is Brush brush)
        {
            SidebarStatusDot.Fill = brush;
        }

        UpdateSidebarStatusTooltip();
    }

    internal void UpdateSidebarStatusTooltip()
    {
        if (SidebarStatusBorder is null || SidebarStatusLabel is null)
        {
            return;
        }

        var statusLabel = SidebarStatusLabel.Text ?? "Ready";
        var clockText = SessionPage.SessionClockText?.Text ?? "00:00:00";
        var accountLabel = ApiKeyVault.ActiveEntry?.DisplayLabel ?? "None";
        var hotkeys = SidebarHotkeyHint?.Text ?? string.Empty;
        var tooltip = $"Status: {statusLabel}\nClock: {clockText}\nAccount: {accountLabel}";
        SidebarStatusBorder.ToolTip = string.IsNullOrWhiteSpace(hotkeys) ? tooltip : $"{tooltip}\n{hotkeys}";
    }

    private static string ProfileFile(string fileName) => Path.Combine(RuntimePaths.Create().Profiles, fileName);

    private static string LimitsPath => ProfileFile("trading-limits.json");
    private static string ClientSettingsPath => ProfileFile("client-settings.json");
    private static string OperatorSettingsPath => ProfileFile("operator-settings.json");
    private static string ApiKeyStorePath => ProfileFile("gw2-api-key.dpapi");
    private static string ApiKeyVaultPath => ProfileFile("gw2-api-keys.dpapi");
    internal static string CustomShoppingListPath => ProfileFile("custom-shoppinglist.json");
    internal static string BlacklistStorePath => ProfileFile("blacklist.json");
    internal static string ScannedCandidatesPath => ProfileFile("scanned-candidates.json");
    private static string LedgerDbPath => RuntimePaths.Create().Database;

    // ---------------------------------------------------------------------
    // Max Cap Completion Countdown
    // ---------------------------------------------------------------------

    internal void HandleSessionCapCompletion(SessionReportNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        if (string.Equals(LastHandledCapReportId, notification.Report.Id, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        LastHandledCapReportId = notification.Report.Id;
        if (!notification.ShutdownPc && !notification.AutoCloseApp)
        {
            return;
        }

        var countdownSeconds = notification.ShutdownPc
            ? Math.Clamp(notification.ShutdownCountdownSeconds, 10, 600)
            : 15;
        var capNote = $"Reached the {SessionReportFormatter.FormatDuration(notification.Report.DurationSeconds)} session cap";
        var summaryNote = notification.Saved ? $"{capNote} · report saved in Session Reports" : capNote;

        if (!IsVisible)
        {
            // Headless unit test path: execute injected delegate cleanly without blocking UI thread.
            if (notification.ShutdownPc)
            {
                _executeSystemShutdown(countdownSeconds);
            }
            else
            {
                _requestAppClose();
            }

            return;
        }

        var dialog = new SessionCompletionWindow(
            isPcShutdown: notification.ShutdownPc,
            countdownSeconds: countdownSeconds,
            summaryNote: summaryNote,
            onExecutePrimary: () =>
            {
                if (notification.ShutdownPc)
                {
                    _executeSystemShutdown(5);
                }
                else
                {
                    _requestAppClose();
                }
            },
            onCloseAppOnly: _requestAppClose,
            report: notification.Report)
        {
            Owner = this
        };
        dialog.Show();
    }

    private void TriggerWindowsSystemShutdown(int delaySeconds)
    {
        var delay = Math.Clamp(delaySeconds, 5, 600);
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "shutdown.exe",
                Arguments = $"/s /t {delay} /c \"Mmopal completed your Guild Wars 2 trade session and is shutting down safely.\"",
                CreateNoWindow = true,
                UseShellExecute = false
            });
        }
        catch (Exception ex)
        {
            AddLog($"ERROR scheduling PC shutdown: {ex.Message}");
        }

        Close();
    }

}
