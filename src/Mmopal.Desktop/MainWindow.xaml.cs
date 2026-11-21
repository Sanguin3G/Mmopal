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

        SessionPage.SessionView.Visibility = NavSession.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        IncomePage.IncomeView.Visibility = NavIncome.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ReportsPage.ReportsView.Visibility = NavReports.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ScannerPage.ScannerView.Visibility = NavScanner.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ListsPage.ListsView.Visibility = NavLists.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.SettingsView.Visibility = NavSettings.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        GuidePage.GuideView.Visibility = NavGuide.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        if (NavReports.IsChecked == true)
        {
            _ = ReportsPage.LoadSessionReportsAsync();
        }
    }


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

        SessionPage.StartSessionButton.IsEnabled = !isActive && !isPaused;
        SessionPage.StartSessionButton.Content = isActive
            ? "Running..."
            : isPaused
                ? "Paused"
                : "Start session";
        SessionPage.PauseSessionButton.IsEnabled = isActive;
        SessionPage.ResumeSessionButton.IsEnabled = isPaused;
        SessionPage.StopSessionButton.IsEnabled = isActive || isPaused;
        SessionPage.EmergencyStopButton.IsEnabled = isActive || isPaused;
        SessionPage.TryOfflineButton.IsEnabled = !isActive && !isPaused;
        SessionPage.SwapCharacterNowButton.IsEnabled = !isActive && !isPaused;

        var selectorsUnlocked = !isActive && !isPaused;
        SessionPage.BotSelector.IsEnabled = selectorsUnlocked;
        SessionPage.ModeSelector.IsEnabled = selectorsUnlocked;
        SessionPage.ProfileSelector.IsEnabled = selectorsUnlocked;
        SessionPage.SourceSelector.IsEnabled = selectorsUnlocked;
        SessionPage.ArmLiveInputCheckBox.IsEnabled = selectorsUnlocked;
        SessionPage.AutoSellInventoryCheckBox.IsEnabled = selectorsUnlocked;

        if (isActive && SettingsPage.AutoDockBesideGw2CheckBox.IsChecked == true)
        {
            ApplySideDockBesideGw2();
        }
        else if (state is WorkerState.Stopped or WorkerState.Idle or WorkerState.Faulted or WorkerState.NeedsAttention)
        {
            RestorePreDockBounds();
        }

        if (isActive)
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

            SessionPage.UpdateSessionClockDisplay();
        }
        else if (state is WorkerState.Idle or WorkerState.Stopped or WorkerState.Faulted or WorkerState.NeedsAttention)
        {
            _sessionClockTimer.Stop();
            SessionPage.UpdateSessionClockDisplay();
        }
        else if (state == WorkerState.Paused)
        {
            SessionPage.UpdateSessionClockDisplay();
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

        if (SessionPage.SessionHeaderPills is not null)
        {
            // Docked: pills go under the title so "Session Control Room" is not squeezed to "Session...".
            Grid.SetRow(SessionPage.SessionHeaderPills, compact ? 1 : 0);
            Grid.SetColumn(SessionPage.SessionHeaderPills, compact ? 0 : 1);
            Grid.SetColumnSpan(SessionPage.SessionHeaderPills, compact ? 2 : 1);
            SessionPage.SessionHeaderPills.HorizontalAlignment = compact ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            SessionPage.SessionHeaderPills.Margin = compact ? new Thickness(0, 8, 0, 0) : new Thickness(0);
        }

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
        if (SessionPage.SessionSubtitleText is not null) SessionPage.SessionSubtitleText.Visibility = labelVisibility;
        if (IncomePage.IncomeSubtitleText is not null) IncomePage.IncomeSubtitleText.Visibility = labelVisibility;
        if (GuidePage.GuideScheduleGrid is not null) GuidePage.GuideScheduleGrid.Columns = cardColumns;
        ScannerPage.ScannerCoreFiltersGrid.Columns = columns;
        ScannerPage.ScannerAdvancedFiltersGrid.Columns = columns;
        if (ReportsPage.ReportDetailKpiGrid is not null)
        {
            ReportsPage.ReportDetailKpiGrid.Columns = columns;
        }

        if (IncomePage.IncomeHeaderActionsPanel is not null)
        {
            if (compact)
            {
                Grid.SetRow(IncomePage.IncomeHeaderActionsPanel, 1);
                Grid.SetColumn(IncomePage.IncomeHeaderActionsPanel, 0);
                Grid.SetColumnSpan(IncomePage.IncomeHeaderActionsPanel, 2);
                IncomePage.IncomeHeaderActionsPanel.HorizontalAlignment = HorizontalAlignment.Left;
                IncomePage.IncomeHeaderActionsPanel.Margin = new Thickness(0, 10, 0, 0);
            }
            else
            {
                Grid.SetRow(IncomePage.IncomeHeaderActionsPanel, 0);
                Grid.SetColumn(IncomePage.IncomeHeaderActionsPanel, 1);
                Grid.SetColumnSpan(IncomePage.IncomeHeaderActionsPanel, 1);
                IncomePage.IncomeHeaderActionsPanel.HorizontalAlignment = HorizontalAlignment.Right;
                IncomePage.IncomeHeaderActionsPanel.Margin = new Thickness(0);
            }
        }

        if (IncomePage.IncomeChartsLeftCol is not null && IncomePage.IncomeChartsRightCol is not null &&
            IncomePage.DailyProfitChartCard is not null && IncomePage.HourlyActivityChartCard is not null)
        {
            // Narrow windows show one chart (its selector also offers "Activity by hour") so the tables keep room.
            if (compact)
            {
                IncomePage.IncomeChartsLeftCol.Width = new GridLength(1, GridUnitType.Star);
                IncomePage.IncomeChartsRightCol.Width = new GridLength(0);
                Grid.SetRow(IncomePage.DailyProfitChartCard, 0);
                Grid.SetColumn(IncomePage.DailyProfitChartCard, 0);
                IncomePage.DailyProfitChartCard.Margin = new Thickness(0);
                IncomePage.HourlyActivityChartCard.Visibility = Visibility.Collapsed;
            }
            else
            {
                IncomePage.IncomeChartsLeftCol.Width = new GridLength(1.2, GridUnitType.Star);
                IncomePage.IncomeChartsRightCol.Width = new GridLength(1, GridUnitType.Star);
                Grid.SetRow(IncomePage.DailyProfitChartCard, 0);
                Grid.SetColumn(IncomePage.DailyProfitChartCard, 0);
                IncomePage.DailyProfitChartCard.Margin = new Thickness(0, 0, 12, 0);
                Grid.SetRow(IncomePage.HourlyActivityChartCard, 0);
                Grid.SetColumn(IncomePage.HourlyActivityChartCard, 1);
                IncomePage.HourlyActivityChartCard.Visibility = Visibility.Visible;
            }
        }

        if (ListsPage.ListsLeftCol is not null && ListsPage.ListsRightCol is not null &&
            ListsPage.ListsTopRow is not null && ListsPage.ListsBottomRow is not null &&
            ListsPage.CustomListCard is not null && ListsPage.BlacklistCard is not null)
        {
            if (mediumCompact)
            {
                ListsPage.ListsLeftCol.Width = new GridLength(1, GridUnitType.Star);
                ListsPage.ListsRightCol.Width = new GridLength(0);
                ListsPage.ListsTopRow.Height = new GridLength(1, GridUnitType.Star);
                ListsPage.ListsBottomRow.Height = new GridLength(1, GridUnitType.Star);
                Grid.SetRow(ListsPage.CustomListCard, 0);
                Grid.SetColumn(ListsPage.CustomListCard, 0);
                ListsPage.CustomListCard.Margin = new Thickness(0, 0, 0, 12);
                Grid.SetRow(ListsPage.BlacklistCard, 1);
                Grid.SetColumn(ListsPage.BlacklistCard, 0);
            }
            else
            {
                ListsPage.ListsLeftCol.Width = new GridLength(1.2, GridUnitType.Star);
                ListsPage.ListsRightCol.Width = new GridLength(1, GridUnitType.Star);
                ListsPage.ListsTopRow.Height = new GridLength(1, GridUnitType.Star);
                ListsPage.ListsBottomRow.Height = GridLength.Auto;
                Grid.SetRow(ListsPage.CustomListCard, 0);
                Grid.SetColumn(ListsPage.CustomListCard, 0);
                ListsPage.CustomListCard.Margin = new Thickness(0, 0, 14, 0);
                Grid.SetRow(ListsPage.BlacklistCard, 0);
                Grid.SetColumn(ListsPage.BlacklistCard, 1);
            }
        }

        if (ReportsPage.ReportsLeftCol is not null && ReportsPage.ReportsRightCol is not null &&
            ReportsPage.ReportsTopRow is not null && ReportsPage.ReportsBottomRow is not null &&
            ReportsPage.ReportsListCard is not null && ReportsPage.ReportDetailCard is not null)
        {
            if (compact)
            {
                ReportsPage.ReportsLeftCol.Width = new GridLength(1, GridUnitType.Star);
                ReportsPage.ReportsRightCol.Width = new GridLength(0);
                ReportsPage.ReportsTopRow.Height = new GridLength(200);
                ReportsPage.ReportsBottomRow.Height = new GridLength(1, GridUnitType.Star);
                Grid.SetRow(ReportsPage.ReportsListCard, 0);
                Grid.SetColumn(ReportsPage.ReportsListCard, 0);
                ReportsPage.ReportsListCard.Margin = new Thickness(0, 0, 0, 12);
                Grid.SetRow(ReportsPage.ReportDetailCard, 1);
                Grid.SetColumn(ReportsPage.ReportDetailCard, 0);
            }
            else
            {
                ReportsPage.ReportsLeftCol.Width = new GridLength(300);
                ReportsPage.ReportsRightCol.Width = new GridLength(1, GridUnitType.Star);
                ReportsPage.ReportsTopRow.Height = new GridLength(1, GridUnitType.Star);
                ReportsPage.ReportsBottomRow.Height = GridLength.Auto;
                Grid.SetRow(ReportsPage.ReportsListCard, 0);
                Grid.SetColumn(ReportsPage.ReportsListCard, 0);
                ReportsPage.ReportsListCard.Margin = new Thickness(0, 0, 14, 0);
                Grid.SetRow(ReportsPage.ReportDetailCard, 0);
                Grid.SetColumn(ReportsPage.ReportDetailCard, 1);
            }
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
            var status = JsonSerializer.Deserialize<WorkerStatus>(workerEvent.Payload);
            if (status is not null)
            {
                LastWorkerState = status.State;
                SessionPage.StatusText.Text = status.Message ?? status.LastAction ?? string.Empty;
                var stateLabel = SessionPage.UpdateStatusCard(status.State, status.Message ?? status.LastAction);
                UpdateSidebarStatus(status.State, stateLabel);
                UpdateSessionButtonStates(status.State);
                var statusDetail = status.Message ?? status.LastAction;
                AddLog(
                    string.IsNullOrWhiteSpace(statusDetail)
                        ? $"Session state: {status.State}"
                        : $"Status ({status.State}): {statusDetail}",
                    rawLog);
            }
            else
            {
                SessionPage.StatusText.Text = workerEvent.Payload;
                AddLog(rawLog, rawLog);
            }
        }
        else if (workerEvent.Type == WorkerEventTypes.Health)
        {
            using var document = JsonDocument.Parse(workerEvent.Payload);
            var root = document.RootElement;
            var src = root.TryGetProperty("source", out var source) ? source.GetString() ?? "-" : "-";
            SessionPage.ObservationSourceText.Text = src;
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

            if (root.TryGetProperty("visiblePrice", out var visiblePrice) &&
                visiblePrice.ValueKind == JsonValueKind.Object &&
                visiblePrice.TryGetProperty("price", out var price) &&
                price.ValueKind == JsonValueKind.Object &&
                price.TryGetProperty("copper", out var copper))
            {
                SessionPage.ObservationPriceText.Text = copper.ToString();
            }
            else
            {
                SessionPage.ObservationPriceText.Text = "-";
            }

            var timingSummary = "-";
            if (root.TryGetProperty("timings", out var timings))
            {
                if (timings.ValueKind == JsonValueKind.Object)
                {
                    var parts = new List<string>();
                    if (timings.TryGetProperty("api", out var apiMs)) parts.Add($"api {FormatMs(apiMs)}");
                    if (timings.TryGetProperty("ui", out var uiMs)) parts.Add($"ui {FormatMs(uiMs)}");
                    timingSummary = parts.Count > 0 ? string.Join(" · ", parts) : timings.ToString();
                }
                else
                {
                    timingSummary = timings.ToString();
                }
            }

            SessionPage.ObservationTimingText.Text = timingSummary;
            SessionPage.ObservationTimingText.ToolTip = "Last cycle: GW2 API round trip and screen read time.";

            var buyPausedLowGold = root.TryGetProperty("buyPausedLowGold", out var lowGoldProp) &&
                                   lowGoldProp.ValueKind == JsonValueKind.True;
            var orderBookText = root.TryGetProperty("orderBook", out var orderBook) && orderBook.ValueKind == JsonValueKind.String
                ? orderBook.GetString()
                : null;
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

            if (root.TryGetProperty("characterSwap", out var swap) && swap.ValueKind == JsonValueKind.String)
            {
                SessionPage.ShowSwapStatus(swap.GetString() ?? "AFK swap disabled");
            }

            if (root.TryGetProperty("sessionCap", out var sessionCap) && sessionCap.ValueKind == JsonValueKind.String)
            {
                SessionPage.SessionCapStatusText.Text = sessionCap.GetString() ?? "Max session cap disabled";
            }

            if (root.TryGetProperty("marketList", out var marketList) && marketList.ValueKind == JsonValueKind.String)
            {
                SessionPage.MarketListStatusText.Text = $"Shopping list: {marketList.GetString()}";
            }

            if (root.TryGetProperty("portfolio", out var portfolioProp) && portfolioProp.ValueKind == JsonValueKind.String)
            {
                SessionPage.PortfolioStatusText.Text = portfolioProp.GetString() ?? "History: —";
            }

            if (root.TryGetProperty("sessionStartedAt", out var startedProp) &&
                startedProp.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(startedProp.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var startedAt))
            {
                SessionStartedAtUtc = startedAt;
            }

            if (root.TryGetProperty("sessionExpiresAt", out var expiresProp))
            {
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

            SessionPage.UpdateSessionClockDisplay();

            var decCount = root.TryGetProperty("decisions", out var decProp) && decProp.ValueKind == JsonValueKind.Number
                ? decProp.GetInt32()
                : 0;
            var actCount = root.TryGetProperty("actions", out var actProp) && actProp.ValueKind == JsonValueKind.Number
                ? actProp.GetInt32()
                : 0;
            var soldCount = root.TryGetProperty("soldCount", out var soldProp) && soldProp.ValueKind == JsonValueKind.Number
                ? soldProp.GetInt32()
                : 0;
            var canceledOutbid = root.TryGetProperty("canceledOutbidBuys", out var cancelProp) && cancelProp.ValueKind == JsonValueKind.Number
                ? cancelProp.GetInt32()
                : 0;
            var healthErr = root.TryGetProperty("error", out var errProp) && errProp.ValueKind == JsonValueKind.String
                ? errProp.GetString()
                : null;

            var summaryParts = new List<string>
            {
                $"Cycle [{src}]",
                $"{decCount} evaluated",
                $"{actCount} actions"
            };
            if (tpOpen.HasValue) summaryParts.Insert(1, tpOpen.Value ? "TP open" : "TP closed");
            if (soldCount > 0) summaryParts.Add($"{soldCount} listed sell");
            if (canceledOutbid > 0) summaryParts.Add($"{canceledOutbid} outbid buys refreshed");
            if (buyPausedLowGold) summaryParts.Add("buys paused (0g/floor)");
            if (!string.IsNullOrWhiteSpace(timingSummary) && timingSummary != "-") summaryParts.Add(timingSummary);
            if (!string.IsNullOrWhiteSpace(healthErr)) summaryParts.Add($"error: {healthErr}");

            AddLog(string.Join(" • ", summaryParts), rawLog);
        }
        else if (workerEvent.Type == WorkerEventTypes.SessionReport)
        {
            try
            {
                var notification = JsonSerializer.Deserialize<SessionReportNotification>(workerEvent.Payload, PayloadJson);
                if (notification is not null)
                {
                    var rpt = notification.Report;
                    var dur = SessionReportFormatter.FormatDuration(rpt.DurationSeconds);
                    var msg = notification.Saved
                        ? $"Session report saved [{rpt.Outcome}] — {rpt.TotalTradeActions} trade action(s), {rpt.DeliveryCollections} pickup(s), {rpt.CharacterSwapsCompleted} swap(s), duration {dur}."
                        : $"Session finished [{rpt.Outcome}] in {dur} (not saved per report eligibility rules).";
                    AddLog(msg, rawLog);
                    _ = ReportsPage.LoadSessionReportsAsync();
                    if (notification.CapReached && (notification.ShutdownPc || notification.AutoCloseApp))
                    {
                        HandleSessionCapCompletion(notification);
                    }
                    return;
                }
            }
            catch (JsonException)
            {
                // Fall back to raw logging below if payload shape was unexpected.
            }

            AddLog(rawLog, rawLog);
        }
        else
        {
            AddLog(rawLog, rawLog);
        }
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

    private static string FormatMs(JsonElement value) =>
        value.TryGetDouble(out var ms)
            ? ms >= 1000 ? (ms / 1000).ToString("0.0", CultureInfo.InvariantCulture) + "s" : ms.ToString("0", CultureInfo.InvariantCulture) + "ms"
            : value.ToString();

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
        HasVerifiedApiKey = ApiKeyVault.ActiveEntry?.Status.Equals("VALID", StringComparison.OrdinalIgnoreCase) == true;
        if (!string.IsNullOrWhiteSpace(ApiKeyVault.ActiveEntry?.LaunchPlatform))
        {
            var wasLoading = IsLoadingUi;
            IsLoadingUi = true;
            SelectComboByTag(SettingsPage.LaunchPlatformSelector, ApiKeyVault.ActiveEntry.LaunchPlatform);
            IsLoadingUi = wasLoading;
        }

        RebuildApiKeyViews();
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
                var selectedIdx = 0;
                for (var i = 0; i < ApiKeyVault.Entries.Count; i++)
                {
                    var entry = ApiKeyVault.Entries[i];
                    SidebarAccountSelector.Items.Add(new ComboBoxItem
                    {
                        Content = entry.DisplayLabel,
                        Tag = entry.Id
                    });
                    if (string.Equals(entry.Id, activeId, StringComparison.OrdinalIgnoreCase))
                    {
                        selectedIdx = i;
                    }
                }

                SidebarAccountSelector.SelectedIndex = selectedIdx;
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
        if (string.Equals(ApiKeyVault.ActiveKeyId, keyId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!ApiKeyVault.Entries.Any(e => string.Equals(e.Id, keyId, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        ApiKeyVault = ApiKeyVault with { ActiveKeyId = keyId };
        SaveApiKeyVault(ApiKeyVault);
        HasVerifiedApiKey = ApiKeyVault.ActiveEntry?.Status.Equals("VALID", StringComparison.OrdinalIgnoreCase) == true;
        if (!string.IsNullOrWhiteSpace(ApiKeyVault.ActiveEntry?.LaunchPlatform))
        {
            var wasLoading = IsLoadingUi;
            IsLoadingUi = true;
            SelectComboByTag(SettingsPage.LaunchPlatformSelector, ApiKeyVault.ActiveEntry.LaunchPlatform);
            IsLoadingUi = wasLoading;
        }

        RebuildApiKeyViews();
        RefreshReadinessChecklist();
        SettingsPage.SaveAllSettingsSilently();

        var activeLabel = ApiKeyVault.ActiveEntry?.DisplayLabel ?? "Account";
        AddLog($"Switched active account to {activeLabel}.");
        await IncomePage.ReloadLedgerForActiveAccountAsync();
        await ReportsPage.LoadSessionReportsAsync();

        if (Supervisor is DesktopWorkerSupervisor concreteSupervisor && LastWorkerState is WorkerState.Idle or WorkerState.Stopped)
        {
            await concreteSupervisor.RestartWorkerAsync();
        }
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
        HasVerifiedApiKey = ApiKeyVault.ActiveEntry?.Status.Equals("VALID", StringComparison.OrdinalIgnoreCase) == true;
        RebuildApiKeyViews();
        RefreshReadinessChecklist();
        AddLog("Removed API key from DPAPI vault.");

        await IncomePage.ReloadLedgerForActiveAccountAsync();
        await ReportsPage.LoadSessionReportsAsync();
        if (Supervisor is DesktopWorkerSupervisor concreteSupervisor && LastWorkerState is WorkerState.Idle or WorkerState.Stopped)
        {
            await concreteSupervisor.RestartWorkerAsync();
        }
    }

    internal void RegisterConfiguredHotkeys(bool logWarnings)
    {
        var pauseKey = NullIfBlank(SettingsPage.PauseHotkeyBox.Text);
        var stopKey = NullIfBlank(SettingsPage.StopHotkeyBox.Text);
        var emergencyKey = NullIfBlank(SettingsPage.EmergencyStopHotkeyBox.Text);
        var warnings = _hotkeys.RegisterSet(
            pauseKey,
            () => _ = Dispatcher.InvokeAsync(async () =>
            {
                var cmd = LastWorkerState == WorkerState.Paused ? WorkerCommandTypes.Resume : WorkerCommandTypes.Pause;
                await SendAsync(new WorkerCommand(cmd));
            }),
            stopKey,
            () => _ = Dispatcher.InvokeAsync(async () => await SendAsync(new WorkerCommand(WorkerCommandTypes.Stop))),
            emergencyKey,
            () => _ = Dispatcher.InvokeAsync(async () => await SendAsync(new WorkerCommand(WorkerCommandTypes.EmergencyStop))));

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
        SidebarStatusBorder.ToolTip = string.IsNullOrWhiteSpace(hotkeys)
            ? $"Status: {statusLabel}\nClock: {clockText}\nAccount: {accountLabel}"
            : $"Status: {statusLabel}\nClock: {clockText}\nAccount: {accountLabel}\n{hotkeys}";
    }

    private static string LimitsPath => Path.Combine(RuntimePaths.Create().Profiles, "trading-limits.json");
    private static string ClientSettingsPath => Path.Combine(RuntimePaths.Create().Profiles, "client-settings.json");
    private static string OperatorSettingsPath => Path.Combine(RuntimePaths.Create().Profiles, "operator-settings.json");
    private static string ApiKeyStorePath => Path.Combine(RuntimePaths.Create().Profiles, "gw2-api-key.dpapi");
    private static string ApiKeyVaultPath => Path.Combine(RuntimePaths.Create().Profiles, "gw2-api-keys.dpapi");
    internal static string CustomShoppingListPath => Path.Combine(RuntimePaths.Create().Profiles, "custom-shoppinglist.json");
    internal static string BlacklistStorePath => Path.Combine(RuntimePaths.Create().Profiles, "blacklist.json");

    internal static string ScannedCandidatesPath => Path.Combine(RuntimePaths.Create().Profiles, "scanned-candidates.json");
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
        var summaryNote = notification.Saved
            ? $"Session finished after {SessionReportFormatter.FormatDuration(notification.Report.DurationSeconds)} — report archived."
            : $"Session finished after {SessionReportFormatter.FormatDuration(notification.Report.DurationSeconds)}.";

        if (!IsVisible)
        {
            // Headless unit test path: execute injected delegate cleanly without blocking UI thread.
            if (notification.ShutdownPc)
            {
                _executeSystemShutdown(countdownSeconds);
            }
            else if (notification.AutoCloseApp)
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
            onCloseAppOnly: _requestAppClose)
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
