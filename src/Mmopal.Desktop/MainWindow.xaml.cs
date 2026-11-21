using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Mmopal.Contracts;
using Mmopal.Gw2;
using Mmopal.Infrastructure;
using Mmopal.Native;
using Mmopal.Worker;
using static Mmopal.Desktop.UiHelpers;

namespace Mmopal.Desktop;

public partial class MainWindow : Window
{
    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web);
    private readonly IDesktopWorkerSupervisor _supervisor;
    private readonly Func<TradingLimits> _loadLimits;
    private readonly Action<TradingLimits> _saveLimits;
    private readonly Func<Gw2ClientSettings> _loadClientSettings;
    private readonly Action<Gw2ClientSettings> _saveClientSettings;
    private readonly Func<OperatorSettings> _loadOperatorSettings;
    private readonly Action<OperatorSettings> _saveOperatorSettings;
    private readonly IGw2ClientPreparer _clientPreparer;
    private readonly Func<string?> _detectGw2Path;
    private readonly Func<IReadOnlyList<Gw2InstallCandidate>> _discoverGw2Installs;
    internal Func<DisplayScaleInfo> DetectDisplayScale { get; }
    internal TradingLedgerStore? LedgerStore { get; }
    internal SessionReportStore? SessionReportStore { get; }
    internal IGw2MarketScanner MarketScanner { get; }
    private readonly Func<Gw2ApiKeyVaultData> _loadApiKeyVault;
    private readonly Action<Gw2ApiKeyVaultData> _saveApiKeyVault;
    private readonly Action _requestAppClose;
    private readonly Action<int> _executeSystemShutdown;
    private readonly GlobalHotkeyService _hotkeys = new();
    internal HttpClient Gw2HttpClient { get; } = new() { BaseAddress = new Uri("https://api.guildwars2.com/"), Timeout = TimeSpan.FromSeconds(120) };
    private readonly DispatcherTimer _cycleTimer;
    private readonly DispatcherTimer _sessionClockTimer;

    private readonly ObservableCollection<ApiKeyRowViewModel> _apiKeyRows = new();
    private readonly List<(DateTime Timestamp, string Human, string Raw)> _logEntries = new();

    internal Gw2ApiKeyVaultData ApiKeyVault { get; set; } = new(null, Array.Empty<Gw2ApiKeyEntry>());
    private bool _bannerDismissed;
    internal bool HasVerifiedApiKey { get; set; }
    private Rect? _preDockBounds;
    private WindowState? _preDockWindowState;
    private bool _isSideDocked;
    private DateTimeOffset? _sessionStartedAtUtc;
    private DateTimeOffset? _sessionExpiresAtUtc;
    private string? _lastHandledCapReportId;

    private WorkerState _lastWorkerState = WorkerState.Idle;
    internal bool IsLoadingUi { get; set; }
    // Launch automation (account sync, first-time Auto-Tune, market scan) runs only in the real app, not in tests.
    private static bool s_operatorSettingsExistedAtLaunch = true;
    private bool _runLaunchAutomation;
    private bool _isFirstRun;
    private string? _lastAutoTunedPreset;
    // Auto-Tune fields the operator set by hand; Auto-Tune keeps them (the 25g gold floor was reset to 80g before).
    private readonly HashSet<string> _autoTuneKeeps = new(StringComparer.OrdinalIgnoreCase);
    private bool _applyingAutoTune;

    private static readonly IReadOnlyDictionary<string, string> AutoTuneFieldLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["MinProfit"] = "Minimum profit %",
        ["MaxBuy"] = "Max buy price",
        ["Quantity"] = "Default quantity",
        ["GoldFloor"] = "Gold floor",
        ["Overbid"] = "Overbid",
        ["Undercut"] = "Undercut"
    };

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
        _runLaunchAutomation = true;
        _isFirstRun = !s_operatorSettingsExistedAtLaunch;
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
        _supervisor = supervisor;
        _loadLimits = loadLimits;
        _saveLimits = saveLimits;
        _loadClientSettings = loadClientSettings ?? (() => new Gw2ClientSettings());
        _saveClientSettings = saveClientSettings ?? (_ => { });
        _loadOperatorSettings = loadOperatorSettings ?? (() => new OperatorSettings());
        _saveOperatorSettings = saveOperatorSettings ?? (_ => { });
        _clientPreparer = clientPreparer ?? new Gw2ClientPreparer();
        _detectGw2Path = detectGw2Path ?? (() => null);
        _discoverGw2Installs = discoverGw2Installs ?? (() => []);
        DetectDisplayScale = detectDisplayScale ?? (() => new DisplayScaleInfo(96, 100, true, "100% (96 DPI)"));
        LedgerStore = ledgerStore;
        SessionReportStore = sessionReportStore;
        MarketScanner = marketScanner ?? new Gw2MarketScanner(Gw2HttpClient, LedgerStore);
        _loadApiKeyVault = loadApiKeyVault ?? (() => new Gw2ApiKeyVaultData(null, Array.Empty<Gw2ApiKeyEntry>()));
        _saveApiKeyVault = saveApiKeyVault ?? (_ => { });
        _requestAppClose = requestAppClose ?? Close;
        _executeSystemShutdown = executeSystemShutdown ?? TriggerWindowsSystemShutdown;

        IsLoadingUi = true;
        _cycleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _cycleTimer.Tick += (_, _) => UpdateMarketCycleDisplay();
        _sessionClockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _sessionClockTimer.Tick += (_, _) => UpdateSessionClockDisplay();

        InitializeComponent();
        GuidePage.Attach(this);
        ListsPage.Attach(this);
        ReportsPage.Attach(this);
        ScannerPage.Attach(this);
        IncomePage.Attach(this);
        ApiKeysGrid.ItemsSource = _apiKeyRows;
        ShowRawLogCheckBox.Checked += (_, _) => RebuildLogList();
        ShowRawLogCheckBox.Unchecked += (_, _) => RebuildLogList();

        LoadLimits();
        LoadClientSettings();
        LoadOperatorSettings();
        ListsPage.LoadLists(
            loadCustomList ?? (() => Array.Empty<ItemCandidate>()),
            loadBlacklist ?? (() => Array.Empty<string>()));
        LoadApiKeyVault();
        UpdateMarketCycleDisplay();
        UpdateSessionPaceSummary();
        RefreshReadinessChecklist();
        UpdateSessionButtonStates(WorkerState.Idle);
        UpdateSafeModePill();
        UpdateSidebarStatus(WorkerState.Idle, "Ready (Offline)");
        IsLoadingUi = false;

        _cycleTimer.Start();

        _supervisor.EventReceived += Supervisor_EventReceived;
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
            await RunLaunchAutomationAsync();
        };
        Closed += (_, _) =>
        {
            _cycleTimer.Stop();
            _sessionClockTimer.Stop();
            ScannerPage.CancelRunningScan();
            _hotkeys.Dispose();
            SaveAllSettingsSilently();
            Gw2HttpClient.Dispose();
            var disposeTask = Task.Run(async () => await _supervisor.DisposeAsync().ConfigureAwait(false));
            disposeTask.Wait(TimeSpan.FromSeconds(2));
        };
    }

    // ---------------------------------------------------------------------
    // Navigation & Theme
    // ---------------------------------------------------------------------

    /// <summary>The pages in the content area. Each page's XAML has its own namescope.</summary>
    private IEnumerable<FrameworkElement> Pages => [GuidePage, ListsPage, ReportsPage, ScannerPage, IncomePage];

    /// <summary>Finds a named element in the shell or in any page.</summary>
    internal object? FindElement(string name) =>
        FindName(name) ?? Pages.Select(page => page.FindName(name)).FirstOrDefault(element => element is not null);

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (SessionView is null || IncomePage.IncomeView is null || ReportsPage.ReportsView is null || ScannerPage.ScannerView is null ||
            ListsPage.ListsView is null || SettingsView is null || GuidePage.GuideView is null)
        {
            return;
        }

        SessionView.Visibility = NavSession.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        IncomePage.IncomeView.Visibility = NavIncome.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ReportsPage.ReportsView.Visibility = NavReports.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ScannerPage.ScannerView.Visibility = NavScanner.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ListsPage.ListsView.Visibility = NavLists.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SettingsView.Visibility = NavSettings.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        GuidePage.GuideView.Visibility = NavGuide.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        if (NavReports.IsChecked == true)
        {
            _ = ReportsPage.LoadSessionReportsAsync();
        }
    }

    private void OpenGuide_Click(object sender, RoutedEventArgs e) => NavGuide.IsChecked = true;

    private void DismissReadinessBanner_Click(object sender, RoutedEventArgs e)
    {
        _bannerDismissed = true;
        ReadinessBanner.Visibility = Visibility.Collapsed;
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
        SaveOperatorSettingsIfValid();
    }

    // ---------------------------------------------------------------------
    // Session Controls
    // ---------------------------------------------------------------------

    private void ModeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoadingUi) return;

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
        if (IsLoadingUi || ModeSelector is null) return;

        // Replay data is a recorded sample; arming live clicks against it makes no sense, so fall back to Dry run.
        if (IsSelectedTag(SourceSelector, "replay") && IsSelectedTag(ModeSelector, nameof(RunMode.Live)))
        {
            SelectByTag(ModeSelector, nameof(RunMode.DryRun));
        }
    }

    private void ProfileSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoadingUi) return;
        UpdateSessionPaceSummary();
    }

    private void LaunchPlatformSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoadingUi) return;
        var platformTag = GetSelectedLaunchPlatform();
        if (ApiKeyVault.ActiveEntry is { } active &&
            !string.Equals(active.LaunchPlatform, platformTag, StringComparison.OrdinalIgnoreCase))
        {
            UpdateVaultEntry(active.Id, entry => entry with { LaunchPlatform = platformTag });
            RebuildApiKeyViews();
        }

        UpdateSessionPaceSummary();
        RefreshReadinessChecklist();
        SaveAllSettingsSilently();
    }

    internal string GetSelectedLaunchPlatform() =>
        (LaunchPlatformSelector?.SelectedItem as ComboBoxItem)?.Tag?.ToString() switch
        {
            var tag when string.Equals(tag, Gw2ClientSettings.PlatformSteam, StringComparison.OrdinalIgnoreCase) => Gw2ClientSettings.PlatformSteam,
            _ => Gw2ClientSettings.PlatformArenaNet
        };

    private void UpdateSessionPaceSummary()
    {
        if (SessionPaceSummaryText is null || ProfileSelector is null)
        {
            return;
        }

        var profileTag = (ProfileSelector.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "balanced";
        var paceText = TradingPaceDescriptions.Describe(profileTag);
        var platform = GetSelectedLaunchPlatform();
        var acct = ApiKeyVault.ActiveEntry?.DisplayLabel;
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
        if (IsLoadingUi) return;
        SaveAllSettingsSilently(); // remembered across restarts; it used to reset to off on every launch
    }

    private void ArmLiveInput_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoadingUi) return;
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

        StartSessionButton.IsEnabled = !isActive && !isPaused;
        StartSessionButton.Content = isActive
            ? "Running..."
            : isPaused
                ? "Paused"
                : "Start session";
        PauseSessionButton.IsEnabled = isActive;
        ResumeSessionButton.IsEnabled = isPaused;
        StopSessionButton.IsEnabled = isActive || isPaused;
        EmergencyStopButton.IsEnabled = isActive || isPaused;
        TryOfflineButton.IsEnabled = !isActive && !isPaused;
        SwapCharacterNowButton.IsEnabled = !isActive && !isPaused;

        var selectorsUnlocked = !isActive && !isPaused;
        BotSelector.IsEnabled = selectorsUnlocked;
        ModeSelector.IsEnabled = selectorsUnlocked;
        ProfileSelector.IsEnabled = selectorsUnlocked;
        SourceSelector.IsEnabled = selectorsUnlocked;
        ArmLiveInputCheckBox.IsEnabled = selectorsUnlocked;
        AutoSellInventoryCheckBox.IsEnabled = selectorsUnlocked;

        if (isActive && AutoDockBesideGw2CheckBox.IsChecked == true)
        {
            ApplySideDockBesideGw2();
        }
        else if (state is WorkerState.Stopped or WorkerState.Idle or WorkerState.Faulted or WorkerState.NeedsAttention)
        {
            RestorePreDockBounds();
        }

        if (isActive)
        {
            if (_sessionStartedAtUtc is null)
            {
                _sessionStartedAtUtc = DateTimeOffset.UtcNow;
                if (Gw2ClientSettings.TryParseMaxSessionHours(MaxSessionHoursBox.Text, out var maxMins, out _) && maxMins is > 0)
                {
                    _sessionExpiresAtUtc = _sessionStartedAtUtc.Value.AddMinutes(maxMins.Value);
                }
            }

            if (!_sessionClockTimer.IsEnabled)
            {
                _sessionClockTimer.Start();
            }

            UpdateSessionClockDisplay();
        }
        else if (state is WorkerState.Idle or WorkerState.Stopped or WorkerState.Faulted or WorkerState.NeedsAttention)
        {
            _sessionClockTimer.Stop();
            UpdateSessionClockDisplay();
        }
        else if (state == WorkerState.Paused)
        {
            UpdateSessionClockDisplay();
        }
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ApplyResponsiveLayout(e.NewSize.Width);

    private void DockToggle_Click(object sender, RoutedEventArgs e)
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
            SessionSelectorsGrid is null || SessionKpiGrid is null || IncomePage.IncomeKpiGrid is null ||
            ScannerPage.ScannerCoreFiltersGrid is null || ScannerPage.ScannerAdvancedFiltersGrid is null)
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

        if (SessionHeaderPills is not null)
        {
            // Docked: pills go under the title so "Session Control Room" is not squeezed to "Session...".
            Grid.SetRow(SessionHeaderPills, compact ? 1 : 0);
            Grid.SetColumn(SessionHeaderPills, compact ? 0 : 1);
            Grid.SetColumnSpan(SessionHeaderPills, compact ? 2 : 1);
            SessionHeaderPills.HorizontalAlignment = compact ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            SessionHeaderPills.Margin = compact ? new Thickness(0, 8, 0, 0) : new Thickness(0);
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
        SessionSelectorsGrid.Columns = cardColumns;
        SessionKpiGrid.Columns = cardColumns;
        IncomePage.IncomeKpiGrid.Columns = cardColumns;
        if (SessionSubtitleText is not null) SessionSubtitleText.Visibility = labelVisibility;
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

        var gw2Width = int.TryParse(Gw2WidthBox.Text.Trim(), out var parsedW) && parsedW >= 640 ? parsedW : 1024;
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
        DockToggleButton.Content = "Restore size";
        DockToggleButton.ToolTip = "Restore size: puts Mmopal back to the window size it had before docking.";
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
        DockToggleButton.Content = "Dock beside GW2";
        DockToggleButton.ToolTip = "Dock beside GW2: fits Mmopal next to the 1024x768 game window, or restores your size.";
        ApplyResponsiveLayout(Width);
    }

    private async void LaunchSnapGw2_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadClientSettings(out var clientSettings)) return;
        _saveClientSettings(clientSettings);
        StatusText.Text = $"Preparing Guild Wars 2 window ({clientSettings.WindowWidth}x{clientSettings.WindowHeight} at top-left)...";
        var originalContent = LaunchSnapButton.Content;
        LaunchSnapButton.IsEnabled = false;
        LaunchSnapButton.Content = "Snapping...";
        var succeeded = false;
        try
        {
            var result = await Task.Run(() => _clientPreparer.PrepareAsync(clientSettings, allowLaunch: true)).ConfigureAwait(false);
            var statusMessage = result.Message;
            succeeded = result.WindowReady;

            if (result.WindowReady && _clientPreparer is Gw2ClientPreparer)
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
                    var resnap = await Task.Run(() => _clientPreparer.PrepareAsync(clientSettings, allowLaunch: false)).ConfigureAwait(false);
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

        if (!TryReadClientSettings(out var clientSettings)) return;
        if (clientSettings.ParkedCharacterCount <= 1)
        {
            StatusText.Text = "Set Parked Characters > 1 in Settings before swapping characters.";
            return;
        }

        _saveClientSettings(clientSettings);
        StatusText.Text = "Swapping parked GW2 character (Esc -> Log Out -> target portrait)...";
        var originalContent = SwapCharacterNowButton.Content;
        SwapCharacterNowButton.IsEnabled = false;
        SwapCharacterNowButton.Content = "Swapping...";
        var succeeded = false;

        try
        {
            var prepared = await Task.Run(() => _clientPreparer.PrepareAsync(clientSettings, allowLaunch: false)).ConfigureAwait(false);
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
        TuneBeforeSession();
        if (!TryReadLimits(out var limits)) return;
        if (!TryReadClientSettings(out var clientSettings)) return;
        if (!TryReadOperatorSettings(out var opSettings)) return;

        _saveLimits(limits);
        _saveClientSettings(clientSettings);
        _saveOperatorSettings(opSettings);

        IReadOnlyList<ItemCandidate>? inlineCandidates = opSettings.UseCustomShoppingList && ListsPage.CustomShoppingList.Count > 0
            ? ListsPage.CustomShoppingList.ToArray()
            : null;
        IReadOnlyList<string>? inlineBlacklist = ListsPage.Blacklist.Count > 0
            ? ListsPage.Blacklist.ToArray()
            : null;

        long? minBalanceCopper = opSettings.GoldThresholdNoBuy is { } goldFloor
            ? checked((long)decimal.Round(goldFloor * 10_000m, 0, MidpointRounding.AwayFromZero))
            : null;

        _sessionStartedAtUtc = DateTimeOffset.UtcNow;
        _sessionExpiresAtUtc = clientSettings.MaxSessionDurationMinutes is > 0
            ? _sessionStartedAtUtc.Value.AddMinutes(clientSettings.MaxSessionDurationMinutes.Value)
            : null;
        _lastHandledCapReportId = null;
        UpdateSessionClockDisplay();

        // Re-scan during the session only when it runs on the scanner's own list, so a hand-made list is never replaced.
        MarketScanFilter? autoRescanFilter = null;
        if (ScannerPage.ReadAutoRescanHours() > 0 &&
            string.Equals(Path.GetFullPath(NullIfBlank(CandidatesPathBox.Text) ?? "."), Path.GetFullPath(ScannedCandidatesPath), StringComparison.OrdinalIgnoreCase) &&
            ScannerPage.TryReadScanFilter(out var currentScanFilter, out _))
        {
            autoRescanFilter = currentScanFilter;
        }

        var payload = JsonSerializer.Serialize(new TradingReplayRequest(
            Candidates: inlineCandidates,
            Blacklist: inlineBlacklist,
            Source: source,
            FixturePath: NullIfBlank(FixturePathBox.Text),
            RecordPath: NullIfBlank(RecordPathBox.Text),
            CaptureFrames: CaptureFramesCheckBox.IsChecked == true,
            Policy: limits.ToPolicy(),
            CandidatesPath: NullIfBlank(CandidatesPathBox.Text),
            BlacklistPath: NullIfBlank(BlacklistPathBox.Text),
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
            ActiveAccountName: ResolveActiveSqlAccountName(),
            ReportSettings: opSettings.ToSessionReportPolicy(),
            AutoRescanFilter: autoRescanFilter,
            AutoRescanIntervalMinutes: autoRescanFilter is null ? 0 : (int)Math.Round(ScannerPage.ReadAutoRescanHours() * 60m)), PayloadJson);
        await SendAsync(new WorkerCommand(WorkerCommandTypes.Start, Guid.NewGuid().ToString("N"), bot, mode, profile, payload));
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
        await SendAsync(new WorkerCommand(
            WorkerCommandTypes.Start,
            Guid.NewGuid().ToString("N"),
            BotKind.TradingPost,
            RunMode.DryRun,
            profile,
            JsonSerializer.Serialize(sample, PayloadJson)));
    }

    private async void Pause_Click(object sender, RoutedEventArgs e) => await SendAsync(new WorkerCommand(WorkerCommandTypes.Pause));
    private async void Resume_Click(object sender, RoutedEventArgs e) => await SendAsync(new WorkerCommand(WorkerCommandTypes.Resume));
    private async void Stop_Click(object sender, RoutedEventArgs e) => await SendAsync(new WorkerCommand(WorkerCommandTypes.Stop));
    private async void EmergencyStop_Click(object sender, RoutedEventArgs e) => await SendAsync(new WorkerCommand(WorkerCommandTypes.EmergencyStop));

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        _logEntries.Clear();
        LogList.Items.Clear();
        UpdateLogCount();
    }

    // ---------------------------------------------------------------------
    // Launch automation (account sync, Auto-Tune, market scan)
    // ---------------------------------------------------------------------

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
    private async Task RunLaunchAutomationAsync()
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
        var automate = AutomateOnLaunchCheckBox.IsChecked == true;
        var tuneEachSession = AutoTuneEachSessionCheckBox.IsChecked == true;
        if (!_runLaunchAutomation || (!automate && !tuneEachSession && !_isFirstRun))
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
        else if (string.IsNullOrWhiteSpace(ResolveConfiguredApiKey()) || LedgerStore is null)
        {
            SetLaunchStep(LaunchSyncText, LaunchStep.Skipped, "No API key: account not synced");
        }
        else
        {
            SetLaunchStep(LaunchSyncText, LaunchStep.Running, "Syncing account…");
            SetLaunchStep(LaunchSyncText, await IncomePage.SyncPortfolioCoreAsync() ? LaunchStep.Done : LaunchStep.Failed,
                IncomePage.LatestEquitySnapshot is { } snap ? $"Account synced ({MarketScanItem.FormatCoins(snap.WalletCopper)} wallet)" : "Account sync failed");
        }

        SetLaunchStep(LaunchTuneText, LaunchStep.Running, "Auto-Tuning…");
        var (tuneStep, tuneText) = await CheckAutoTuneOnLaunchAsync();
        SetLaunchStep(LaunchTuneText, tuneStep, tuneText);

        if (!automate)
        {
            SetLaunchStep(LaunchScanText, LaunchStep.Skipped, "Market scan off");
            return;
        }

        if (_lastWorkerState is not (WorkerState.Idle or WorkerState.Stopped))
        {
            SetLaunchStep(LaunchScanText, LaunchStep.Skipped, "Session already running");
            return; // a session was started meanwhile; leave its list alone
        }

        SetLaunchStep(LaunchScanText, LaunchStep.Running, "Scanning the market (~1 min)…");
        if (await ScannerPage.RunMarketScanCoreAsync(useCachedPrices: false))
        {
            var listNote = RefreshSessionListAfterLaunchScan();
            SetLaunchStep(LaunchScanText, LaunchStep.Done, $"{ScannerPage.ScanResults.Count} flips found{listNote}");
        }
        else
        {
            SetLaunchStep(LaunchScanText, LaunchStep.Failed, "Market scan failed (see Market Scanner)");
        }
    }

    private async Task<(LaunchStep Step, string Text)> CheckAutoTuneOnLaunchAsync()
    {
        var wallet = IncomePage.LatestEquitySnapshot?.WalletCopper ?? 0;
        var key = ResolveConfiguredApiKey();
        if (wallet <= 0 && !string.IsNullOrWhiteSpace(key))
        {
            try
            {
                using var cts = CreateApiCancellationTokenSource();
                wallet = (await new Gw2OfficialDataProvider(Gw2HttpClient, key).GetBalanceAsync(cts.Token))?.Copper ?? 0;
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
        if (AutoTuneEachSessionCheckBox.IsChecked == true || (_isFirstRun && _lastAutoTunedPreset is null))
        {
            var tune = await RunAutoTuneAsync(feedbackButton: null);
            return (LaunchStep.Done, $"Auto-Tuned: {ShortPresetName(tune.Preset.Name)}, Max Buy {tune.Preset.MaxBuyGold:0.#}g");
        }

        if (_lastAutoTunedPreset is not null && !string.Equals(_lastAutoTunedPreset, preset.Name, StringComparison.OrdinalIgnoreCase))
        {
            // With Auto-Tune each session off, the operator tunes by hand: only suggest.
            var hint = $"Your wallet ({MarketScanItem.FormatCoins(wallet)}) now fits the '{preset.Name}' preset (last tuned: '{_lastAutoTunedPreset}'). Click Auto-Tune to switch.";
            ScannerPage.ScanStatusText.Text = hint;
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
        if (ScannerPage.ScanResults.Count == 0)
        {
            return string.Empty;
        }

        var listPath = NullIfBlank(CandidatesPathBox.Text);
        if (listPath is null)
        {
            // No list at all: trade the fresh scan, unless a Custom Shopping List is the operator's whole plan.
            if (UseCustomListOnStartCheckBox.IsChecked == true && ListsPage.CustomShoppingList.Count > 0)
            {
                return string.Empty;
            }

            ScannerPage.BindScanResultsToSession();
            AddLog($"Launch: no session list was set, so the fresh scan ({ScannerPage.ScanResults.Count} items) is now the session list.");
            return " · now the session list";
        }

        if (!string.Equals(Path.GetFullPath(listPath), Path.GetFullPath(ScannedCandidatesPath), StringComparison.OrdinalIgnoreCase))
        {
            return " · your own list kept";
        }

        var intervalHours = ScannerPage.ReadAutoRescanHours();
        var age = File.Exists(ScannedCandidatesPath)
            ? DateTime.UtcNow - File.GetLastWriteTimeUtc(ScannedCandidatesPath)
            : TimeSpan.MaxValue;
        if (intervalHours > 0 && age >= TimeSpan.FromHours((double)intervalHours))
        {
            ScannerPage.BindScanResultsToSession();
            AddLog($"Launch: the scanned session list was {FormatAge(age)} old; replaced with today's scan ({ScannerPage.ScanResults.Count} items).");
            return " · session list refreshed";
        }

        return string.Empty;
    }

    private static string FormatAge(TimeSpan age) =>
        age == TimeSpan.MaxValue ? "missing" : age.TotalDays >= 1 ? $"{age.TotalDays:0.#} days" : $"{age.TotalHours:0.#} h";

    // ---------------------------------------------------------------------
    // Auto-Tune (trading values from the wallet and recent results)
    // ---------------------------------------------------------------------

    private async void AutoTuneForWallet_Click(object sender, RoutedEventArgs e) => await AutoTuneForWalletAsync(sender as Button);

    internal async Task AutoTuneForWalletAsync(Button? sender)
    {
        // Clicking Auto-Tune is an explicit "decide for me": hand-set values go back to Auto-Tune. The automatic runs
        // (launch, Start session) keep them.
        if (_autoTuneKeeps.Count > 0)
        {
            AddLog($"Auto-Tune button: your hand-set values ({DescribeAutoTuneKeeps()}) are handed back to Auto-Tune.");
            _autoTuneKeeps.Clear();
            UpdateAutoTuneKeepsHint();
        }

        await RunAutoTuneAsync(sender ?? AutoTuneSettingsButton);
    }

    private async Task<AutoTuneResult> RunAutoTuneAsync(Button? feedbackButton)
    {
        long capitalCopper = IncomePage.LatestEquitySnapshot?.WalletCopper ?? 0;
        var sourceLabel = "cached snapshot";

        var key = ResolveConfiguredApiKey();
        if (!string.IsNullOrWhiteSpace(key))
        {
            try
            {
                using var cts = CreateApiCancellationTokenSource();
                var provider = new Gw2OfficialDataProvider(Gw2HttpClient, key);
                var balance = await provider.GetBalanceAsync(cts.Token);
                if (balance is not null)
                {
                    capitalCopper = balance.Copper;
                    sourceLabel = "live GW2 wallet";
                    HasVerifiedApiKey = true;
                }
            }
            catch (Exception ex)
            {
                AddLog($"Auto-tune wallet fetch note: {ex.Message}");
            }
        }

        if (capitalCopper <= 0)
        {
            capitalCopper = 5_000_000; // 500g balanced default when offline
            sourceLabel = "default 500g balanced baseline (no API wallet yet)";
        }

        var tune = ApplySmartAutoTune(capitalCopper);
        var message = $"Auto-tuned from {sourceLabel}: {tune.Summary}";
        StatusText.Text = message;
        ScannerPage.ScanStatusText.Text = message;
        AddLog($"Auto-Tune ({sourceLabel}): {tune.Summary}");
        if (feedbackButton is not null)
        {
            await FlashButtonFeedbackAsync(feedbackButton, "✓ Tuned");
        }

        return tune;
    }

    /// <summary>Smart Auto-Tune: capital (wallet + gold in orders) plus the last 14 days of your own flips.</summary>
    private AutoTuneResult ApplySmartAutoTune(long walletCopper)
    {
        var snapshot = IncomePage.LatestEquitySnapshot;
        var recent = IncomePage.ArchivedTransactions.Count == 0
            ? null
            : TradingIncomeAnalytics.Analyze(IncomePage.ArchivedTransactions, DateTimeOffset.UtcNow.AddDays(-SmartAutoTuner.ResultsWindowDays));
        var tune = SmartAutoTuner.Tune(new AutoTuneInputs(
            walletCopper,
            snapshot?.ActiveBuysCopper ?? 0,
            snapshot?.ActiveSellsNetCopper ?? 0,
            recent));
        ScannerPage.ApplyStrategyPresetToScanner(tune.Preset);
        ApplyStrategyPresetToSettings(tune.Preset);
        ScannerPage.ScanMinTradesPerDayBox.Text = tune.MinTradesPerDay.ToString(CultureInfo.CurrentCulture);
        _lastAutoTunedPreset = tune.Preset.Name;
        SaveAllSettingsSilently();
        RefreshReadinessChecklist();
        if (_autoTuneKeeps.Count > 0)
        {
            tune = tune with { Summary = $"{tune.Summary} Kept your own: {DescribeAutoTuneKeeps()}." };
        }

        var tip = $"Last Auto-Tune ({DateTime.Now:g}): {tune.Summary}";
        ScannerPage.AutoTuneScannerButton.ToolTip = tip;
        AutoTuneSettingsButton.ToolTip = tip;
        return tune;
    }

    /// <summary>Re-tune right before a session from the last synced wallet and orders (no network call).</summary>
    private void TuneBeforeSession()
    {
        if (AutoTuneEachSessionCheckBox.IsChecked != true || IncomePage.LatestEquitySnapshot is not { WalletCopper: > 0 } snapshot)
        {
            return;
        }

        var tune = ApplySmartAutoTune(snapshot.WalletCopper);
        AddLog($"Auto-Tune before session (gold as of the last account sync): {tune.Summary}");
    }

    private void TunedField_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsLoadingUi || _applyingAutoTune || sender is not TextBox { Tag: string key } || !_autoTuneKeeps.Add(key))
        {
            return;
        }

        UpdateAutoTuneKeepsHint();
    }

    private void ClearAutoTuneKeeps_Click(object sender, RoutedEventArgs e)
    {
        _autoTuneKeeps.Clear();
        UpdateAutoTuneKeepsHint();
        SaveOperatorSettingsIfValid();
        AddLog("Auto-Tune will set every trading value again from the next launch or Start session.");
    }

    private void UpdateAutoTuneKeepsHint()
    {
        if (AutoTuneKeepsPanel is null)
        {
            return;
        }

        AutoTuneKeepsPanel.Visibility = _autoTuneKeeps.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        AutoTuneKeepsText.Text = $"Auto-Tune keeps what you set: {DescribeAutoTuneKeeps()}. It tunes the rest at launch and Start session; clicking Auto-Tune hands everything back.";
    }

    private string DescribeAutoTuneKeeps() =>
        string.Join(", ", _autoTuneKeeps.Select(key => AutoTuneFieldLabels.GetValueOrDefault(key, key)).OrderBy(label => label, StringComparer.Ordinal));

    internal void ApplyStrategyPresetToSettings(TradingStrategyPreset preset)
    {
        _applyingAutoTune = true;
        try
        {
            ApplyStrategyPresetToSettingsCore(preset);
        }
        finally
        {
            _applyingAutoTune = false;
        }
    }

    private void ApplyStrategyPresetToSettingsCore(TradingStrategyPreset preset)
    {
        bool Tunes(string key) => !_autoTuneKeeps.Contains(key);
        if (Tunes("MinProfit")) MinimumProfitBox.Text = preset.MinRoiPercent.ToString(CultureInfo.CurrentCulture);
        if (Tunes("MaxBuy")) MaximumBuyGoldBox.Text = preset.MaxBuyGold.ToString(CultureInfo.CurrentCulture);
        if (Tunes("Quantity")) DefaultQuantityBox.Text = preset.DefaultOrderQuantity.ToString(CultureInfo.InvariantCulture);
        if (Tunes("GoldFloor")) GoldThresholdNoBuyBox.Text = preset.RecommendedGoldFloor.ToString(CultureInfo.CurrentCulture);
        if (Tunes("Overbid"))
        {
            OverbidMinBox.Text = preset.OverbidMinCopper.ToString(CultureInfo.InvariantCulture);
            OverbidMaxBox.Text = preset.OverbidMaxCopper.ToString(CultureInfo.InvariantCulture);
        }

        if (Tunes("Undercut"))
        {
            UndercutMinBox.Text = preset.UndercutMinCopper.ToString(CultureInfo.InvariantCulture);
            UndercutMaxBox.Text = preset.UndercutMaxCopper.ToString(CultureInfo.InvariantCulture);
        }

        // Session cap and character-swap interval are the operator's own schedule, not a function of wallet size,
        // so Auto-Tune leaves them as configured.
    }

    // ---------------------------------------------------------------------
    // Settings: Multi-Account DPAPI API Vault, GW2 Client, Latency, Hotkeys
    // ---------------------------------------------------------------------

    private void OpenArenaNetApiKeys_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://account.arena.net/applications",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AddLog($"Could not open browser: {ex.Message}");
        }
    }

    private async void SidebarAccountSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoadingUi || SidebarAccountSelector.SelectedItem is not ComboBoxItem { Tag: string keyId })
        {
            return;
        }

        await SwitchActiveAccountAsync(keyId);
    }

    private async void UseApiKey_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.CommandParameter is not string keyId) return;
        await SwitchActiveAccountAsync(keyId);
    }

    private void MoveApiKeyUp_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.CommandParameter is string keyId)
        {
            MoveApiKeyEntry(keyId, -1);
        }
    }

    private void MoveApiKeyDown_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.CommandParameter is string keyId)
        {
            MoveApiKeyEntry(keyId, 1);
        }
    }

    private async void CopyApiKey_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.CommandParameter is not string keyId) return;
        var entry = ApiKeyVault.Entries.FirstOrDefault(item => string.Equals(item.Id, keyId, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return;

        try
        {
            Clipboard.SetText(entry.ApiKey);
            ApiKeyStatusText.Text = $"Copied API key for {entry.DisplayLabel} to clipboard.";
            await FlashButtonFeedbackAsync(btn, "✓ Copied");
        }
        catch (Exception ex)
        {
            ApiKeyStatusText.Text = $"Could not copy API key: {ex.Message}";
        }
    }

    private async void DeleteApiKey_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.CommandParameter is not string keyId) return;
        await RemoveVaultEntryAsync(keyId);
    }

    // Names save themselves (Enter or leaving the box). A separate "Save Names" click was easy to miss, and the launch
    // account sync rebuilds this table, which wiped names typed but not yet saved.
    private void ApiKeyName_LostFocus(object sender, RoutedEventArgs e) => CommitApiKeyName(sender as TextBox);

    private void ApiKeyName_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        CommitApiKeyName(sender as TextBox);
        Keyboard.ClearFocus();
        e.Handled = true;
    }

    internal void CommitApiKeyName(TextBox? box)
    {
        if (box?.DataContext is not ApiKeyRowViewModel row)
        {
            return;
        }

        var name = box.Text?.Trim() ?? string.Empty;
        var entry = ApiKeyVault.Entries.FirstOrDefault(e => string.Equals(e.Id, row.Id, StringComparison.OrdinalIgnoreCase));
        if (entry is null || string.Equals(entry.CustomName ?? string.Empty, name, StringComparison.Ordinal))
        {
            return;
        }

        row.CustomName = name;
        ApiKeyVault = ApiKeyVault with
        {
            Entries = ApiKeyVault.Entries
                .Select(e => string.Equals(e.Id, row.Id, StringComparison.OrdinalIgnoreCase) ? e with { CustomName = name } : e)
                .ToArray()
        };
        _saveApiKeyVault(ApiKeyVault);
        ApiKeyStatusText.Text = string.IsNullOrEmpty(name)
            ? $"Removed the name of {entry.AccountName}."
            : $"Saved the name '{name}' for {entry.AccountName}.";
        // Refresh the account picker and labels after the edit, not during it.
        Dispatcher.BeginInvoke(RebuildApiKeyViews, System.Windows.Threading.DispatcherPriority.Background);
    }

    private async void ReverifyApiKeys_Click(object sender, RoutedEventArgs e)
    {
        if (ApiKeyVault.Entries.Count == 0)
        {
            ApiKeyStatusText.Text = "No API keys to verify yet.";
            return;
        }

        var originalContent = ReverifyApiKeysButton.Content;
        ReverifyApiKeysButton.IsEnabled = false;
        ReverifyApiKeysButton.Content = "Verifying...";
        var verifiedCount = 0;
        try
        {
            var refreshed = new List<Gw2ApiKeyEntry>(ApiKeyVault.Entries.Count);
            foreach (var entry in ApiKeyVault.Entries)
            {
                try
                {
                    using var cts = CreateApiCancellationTokenSource();
                    var provider = new Gw2OfficialDataProvider(Gw2HttpClient, entry.ApiKey);
                    var validation = await provider.ValidateApiKeyAsync(cts.Token);
                    var accountName = !string.IsNullOrWhiteSpace(validation.AccountName)
                        ? validation.AccountName!
                        : entry.AccountName;
                    var status = validation.IsValid ? "VALID" : "INVALID";
                    if (validation.IsValid) verifiedCount++;

                    refreshed.Add(entry with
                    {
                        AccountName = accountName,
                        Status = status,
                        Permissions = validation.Permissions,
                        LastWalletCopper = validation.CoinCopper ?? entry.LastWalletCopper,
                        LastVerifiedAt = DateTimeOffset.UtcNow
                    });
                }
                catch
                {
                    refreshed.Add(entry with { Status = "UNVERIFIED" });
                }
            }

            ApiKeyVault = ApiKeyVault with { Entries = refreshed.ToArray() };
            _saveApiKeyVault(ApiKeyVault);
            HasVerifiedApiKey = ApiKeyVault.ActiveEntry?.Status.Equals("VALID", StringComparison.OrdinalIgnoreCase) == true;
            RebuildApiKeyViews();
            RefreshReadinessChecklist();
            ApiKeyStatusText.Text = $"Re-verified {ApiKeyVault.Entries.Count} key(s): {verifiedCount} valid.";
            AddLog($"Re-verified API vault ({verifiedCount}/{ApiKeyVault.Entries.Count} valid).");
        }
        finally
        {
            ReverifyApiKeysButton.IsEnabled = true;
            ReverifyApiKeysButton.Content = originalContent;
            await FlashButtonFeedbackAsync(ReverifyApiKeysButton, "✓ Verified");
        }
    }

    private async void SaveApiKey_Click(object sender, RoutedEventArgs e)
    {
        var raw = ApiKeyPasswordBox.Password?.Trim();
        var customName = ApiKeyCustomNameBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            ApiKeyStatusText.Text = "Enter a GW2 API key before clicking Save.";
            return;
        }

        var originalContent = SaveApiKeyButton.Content;
        SaveApiKeyButton.IsEnabled = false;
        SaveApiKeyButton.Content = "Verifying...";
        var saved = false;
        try
        {
            var existing = ApiKeyVault.Entries.FirstOrDefault(item => string.Equals(item.ApiKey, raw, StringComparison.OrdinalIgnoreCase));
            var entryId = existing?.Id ?? Guid.NewGuid().ToString("N");
            var accountName = existing?.AccountName ?? "Default Account";
            var status = "UNVERIFIED";
            IReadOnlyList<string> permissions = existing?.Permissions ?? Array.Empty<string>();
            long? walletCopper = existing?.LastWalletCopper;
            DateTimeOffset? verifiedAt = existing?.LastVerifiedAt;
            var statusDetail = "Encrypted with Windows DPAPI (CurrentUser) and stored in multi-account vault.";

            try
            {
                using var cts = CreateApiCancellationTokenSource();
                var provider = new Gw2OfficialDataProvider(Gw2HttpClient, raw);
                var result = await provider.ValidateApiKeyAsync(cts.Token);
                if (!string.IsNullOrWhiteSpace(result.AccountName))
                {
                    accountName = result.AccountName!;
                }

                permissions = result.Permissions;
                if (result.IsValid)
                {
                    status = "VALID";
                    walletCopper = result.CoinCopper;
                    verifiedAt = DateTimeOffset.UtcNow;
                    HasVerifiedApiKey = true;
                    statusDetail = $"{accountName}" +
                                   (result.CoinCopper.HasValue ? $" • Wallet {MarketScanItem.FormatCoins(result.CoinCopper.Value)}" : string.Empty) +
                                   " — Verified & saved securely.";
                }
                else
                {
                    status = result.Message.Contains("Invalid", StringComparison.OrdinalIgnoreCase) ||
                             result.Message.Contains("permission", StringComparison.OrdinalIgnoreCase)
                        ? "INVALID"
                        : "UNVERIFIED";
                    statusDetail = $"Saved encrypted key ({status}): {result.Message}";
                }
            }
            catch (Exception ex)
            {
                statusDetail = $"Saved encrypted key (offline/timeout during check: {ex.Message}).";
            }

            var newEntry = new Gw2ApiKeyEntry(
                Id: entryId,
                ApiKey: raw,
                AccountName: accountName,
                CustomName: !string.IsNullOrWhiteSpace(customName) ? customName : (existing?.CustomName ?? string.Empty),
                Status: status,
                Permissions: permissions,
                LastWalletCopper: walletCopper,
                LastVerifiedAt: verifiedAt);

            var updatedEntries = ApiKeyVault.Entries
                .Select(item => string.Equals(item.Id, entryId, StringComparison.OrdinalIgnoreCase) ? newEntry : item)
                .ToList();
            if (existing is null)
            {
                updatedEntries.Add(newEntry);
            }

            ApiKeyVault = new Gw2ApiKeyVaultData(entryId, updatedEntries.ToArray());
            _saveApiKeyVault(ApiKeyVault);

            ApiKeyPasswordBox.Clear();
            ApiKeyCustomNameBox.Clear();
            RebuildApiKeyViews();
            RefreshReadinessChecklist();
            ApiKeyStatusText.Text = statusDetail;
            AddLog($"Saved DPAPI API key for {newEntry.DisplayLabel} ({status}).");
            saved = true;

            await IncomePage.ReloadLedgerForActiveAccountAsync();
            await ReportsPage.LoadSessionReportsAsync();
            if (_supervisor is DesktopWorkerSupervisor concreteSupervisor && _lastWorkerState is WorkerState.Idle or WorkerState.Stopped)
            {
                await concreteSupervisor.RestartWorkerAsync();
            }
        }
        catch (Exception exception)
        {
            ApiKeyStatusText.Text = $"Could not save API key: {exception.Message}";
        }
        finally
        {
            SaveApiKeyButton.IsEnabled = true;
            SaveApiKeyButton.Content = originalContent;
            if (saved)
            {
                await FlashButtonFeedbackAsync(SaveApiKeyButton, "✓ Saved");
            }
        }
    }

    private async void TestApiKey_Click(object sender, RoutedEventArgs e)
    {
        var typedKey = !string.IsNullOrWhiteSpace(ApiKeyPasswordBox.Password)
            ? ApiKeyPasswordBox.Password.Trim()
            : null;
        var activeEntry = ApiKeyVault.ActiveEntry;
        var key = typedKey ?? ResolveConfiguredApiKey();

        if (string.IsNullOrWhiteSpace(key))
        {
            ApiKeyStatusText.Text = "No API key entered or saved yet.";
            return;
        }

        var originalContent = TestApiKeyButton.Content;
        TestApiKeyButton.IsEnabled = false;
        TestApiKeyButton.Content = "Testing...";
        ApiKeyStatusText.Text = "Testing API key against api.guildwars2.com...";
        var isValid = false;
        try
        {
            using var cts = CreateApiCancellationTokenSource();
            var provider = new Gw2OfficialDataProvider(Gw2HttpClient, key);
            var result = await provider.ValidateApiKeyAsync(cts.Token);
            isValid = result.IsValid;

            if (typedKey is null && activeEntry is not null)
            {
                UpdateVaultEntry(activeEntry.Id, item => item with
                {
                    AccountName = !string.IsNullOrWhiteSpace(result.AccountName) ? result.AccountName! : item.AccountName,
                    Status = result.IsValid ? "VALID" : "INVALID",
                    Permissions = result.Permissions,
                    LastWalletCopper = result.CoinCopper ?? item.LastWalletCopper,
                    LastVerifiedAt = DateTimeOffset.UtcNow
                });
                RebuildApiKeyViews();
            }

            if (result.IsValid)
            {
                HasVerifiedApiKey = true;
                var coinText = result.CoinCopper.HasValue ? $" • Wallet: {MarketScanItem.FormatCoins(result.CoinCopper.Value)}" : string.Empty;
                var accountText = !string.IsNullOrWhiteSpace(result.AccountName) ? $"{result.AccountName}" : "Account";
                ApiKeyStatusText.Text = $"{accountText}{coinText} — {result.Message}";
                AddLog($"API key verified: {accountText}{coinText}");
            }
            else
            {
                HasVerifiedApiKey = false;
                ApiKeyStatusText.Text = $"API key test failed: {result.Message}";
            }

            RefreshReadinessChecklist();
        }
        finally
        {
            TestApiKeyButton.IsEnabled = true;
            TestApiKeyButton.Content = originalContent;
            await FlashButtonFeedbackAsync(TestApiKeyButton, isValid ? "✓ Valid" : "Check Failed");
        }
    }

    private async void ClearApiKey_Click(object sender, RoutedEventArgs e)
    {
        ApiKeyPasswordBox.Clear();
        ApiKeyCustomNameBox.Clear();
        if (ApiKeyVault.ActiveEntry is { } active)
        {
            await RemoveVaultEntryAsync(active.Id);
            return;
        }

        HasVerifiedApiKey = false;
        RebuildApiKeyViews();
        RefreshReadinessChecklist();
        AddLog("Cleared stored DPAPI GW2 API key.");
    }

    private void LatencyProfileSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoadingUi || LatencyProfileSelector.SelectedItem is not ComboBoxItem { Tag: string profileId })
        {
            return;
        }

        var preset = Gw2LatencyPresets.FindById(profileId);
        if (preset is null) return;

        LatencyDescriptionText.Text = preset.Description;
        if (!string.Equals(preset.Id, "Custom", StringComparison.OrdinalIgnoreCase))
        {
            UiLoadWaitMsBox.Text = preset.UiLoadWaitMs.ToString(CultureInfo.InvariantCulture);
            ActionSettleDelayMsBox.Text = preset.ActionSettleDelayMs.ToString(CultureInfo.InvariantCulture);
            ApiTimeoutSecondsBox.Text = preset.ApiTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        }
    }

    private async void DetectGw2_Click(object sender, RoutedEventArgs e) => await DetectGw2Async();

    internal async Task DetectGw2Async()
    {
        var candidates = _discoverGw2Installs();
        var detected = candidates.FirstOrDefault()?.ExecutablePath ?? _detectGw2Path();
        var dpi = DetectDisplayScale();

        var found = !string.IsNullOrWhiteSpace(detected);
        if (found)
        {
            Gw2ExecutablePathBox.Text = detected;
            var kindText = candidates.FirstOrDefault() is { } first
                ? $"{first.Kind} install via {first.Source}"
                : "Detected GW2 executable";
            Gw2DetectHealthText.Text = $"{kindText}  •  Display Scale: {dpi.Summary}";
            StatusText.Text = $"Detected Guild Wars 2 at {detected}";
            AddLog($"Detected GW2 ({kindText}): {detected}");
        }
        else
        {
            Gw2DetectHealthText.Text = $"Could not auto-detect Gw2-64.exe  •  Display Scale: {dpi.Summary}";
            StatusText.Text = "Could not auto-detect Gw2-64.exe; click Browse or enter steam://rungameid/1284210.";
        }

        RefreshReadinessChecklist();
        await FlashButtonFeedbackAsync(DetectGw2Button, found ? "✓ Detected" : "Not found");
    }

    private void BrowseGw2_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select Gw2-64.exe",
            Filter = "Guild Wars 2 (Gw2-64.exe)|Gw2-64.exe|Executables (*.exe)|*.exe"
        };
        if (dialog.ShowDialog(this) == true)
        {
            Gw2ExecutablePathBox.Text = dialog.FileName;
            RefreshReadinessChecklist();
        }
    }

    private async void ApplyHotkeys_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadOperatorSettings(out var opSettings)) return;
        _saveOperatorSettings(opSettings);
        RegisterConfiguredHotkeys(logWarnings: true);
        await FlashButtonFeedbackAsync(ApplyHotkeysButton, "✓ Applied");
    }

    private async void SaveAllSettings_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadLimits(out var limits)) return;
        if (!TryReadClientSettings(out var clientSettings)) return;
        if (!TryReadOperatorSettings(out var opSettings)) return;

        _saveLimits(limits);
        _saveClientSettings(clientSettings);
        _saveOperatorSettings(opSettings);
        RegisterConfiguredHotkeys(logWarnings: true);
        RefreshReadinessChecklist();
        StatusText.Text = "All settings saved to local profiles folder.";
        AddLog("Saved trading limits, GW2 client/latency settings, session cap, AFK policy, and hotkeys.");
        await FlashButtonFeedbackAsync(SaveAllSettingsButton, "✓ Saved");
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        var paths = RuntimePaths.Create();
        paths.EnsureDirectories();
        Process.Start(new ProcessStartInfo { FileName = paths.Root, UseShellExecute = true });
    }

    // ---------------------------------------------------------------------
    // Worker Communication & Event Handling
    // ---------------------------------------------------------------------

    private async Task SendAsync(WorkerCommand command)
    {
        try
        {
            var response = await _supervisor.SendAsync(command);
            ApplyEvent(response);
        }
        catch (Exception exception)
        {
            AddLog($"ERROR: {exception.Message}");
            StatusText.Text = "Worker unavailable";
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
                _lastWorkerState = status.State;
                StatusText.Text = status.Message ?? status.LastAction ?? string.Empty;
                var stateLabel = UpdateStatusCard(status.State, status.Message ?? status.LastAction);
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
                StatusText.Text = workerEvent.Payload;
                AddLog(rawLog, rawLog);
            }
        }
        else if (workerEvent.Type == WorkerEventTypes.Health)
        {
            using var document = JsonDocument.Parse(workerEvent.Payload);
            var root = document.RootElement;
            var src = root.TryGetProperty("source", out var source) ? source.GetString() ?? "-" : "-";
            ObservationSourceText.Text = src;
            ObservationSourceText.Text = src switch
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
                ObservationUiText.Text = $"Trading Post: {(open ? "open" : "closed")}; item: {(item ? "open" : "closed")}";
                GameHeadlineText.Text = open ? "TP open" : "TP closed";
                GameHeadlineText.Foreground = open
                    ? ResolveBrush("SuccessBrush", Brushes.SeaGreen)
                    : ResolveBrush("WarningBrush", Brushes.DarkOrange);
                GameCard.ToolTip = ObservationUiText.Text;
            }

            if (root.TryGetProperty("visiblePrice", out var visiblePrice) &&
                visiblePrice.ValueKind == JsonValueKind.Object &&
                visiblePrice.TryGetProperty("price", out var price) &&
                price.ValueKind == JsonValueKind.Object &&
                price.TryGetProperty("copper", out var copper))
            {
                ObservationPriceText.Text = copper.ToString();
            }
            else
            {
                ObservationPriceText.Text = "-";
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

            ObservationTimingText.Text = timingSummary;
            ObservationTimingText.ToolTip = "Last cycle: GW2 API round trip and screen read time.";

            var buyPausedLowGold = root.TryGetProperty("buyPausedLowGold", out var lowGoldProp) &&
                                   lowGoldProp.ValueKind == JsonValueKind.True;
            var orderBookText = root.TryGetProperty("orderBook", out var orderBook) && orderBook.ValueKind == JsonValueKind.String
                ? orderBook.GetString()
                : null;
            if (buyPausedLowGold)
            {
                ObservationOrdersText.Text = !string.IsNullOrWhiteSpace(orderBookText)
                    ? $"Buys paused (0g/reserve floor) • {orderBookText}"
                    : "Buys paused (0g/reserve floor) — sell/collect only";
            }
            else if (orderBookText is not null)
            {
                ObservationOrdersText.Text = orderBookText;
            }

            UpdateOrdersCard(ObservationOrdersText.Text, buyPausedLowGold);

            if (root.TryGetProperty("characterSwap", out var swap) && swap.ValueKind == JsonValueKind.String)
            {
                AfkSwapStatusText.Text = swap.GetString() ?? "AFK swap disabled";
                ScheduleHeadlineText.Text = ShortSwapHeadline(AfkSwapStatusText.Text);
                ScheduleCard.ToolTip = AfkSwapStatusText.Text;
            }

            if (root.TryGetProperty("sessionCap", out var sessionCap) && sessionCap.ValueKind == JsonValueKind.String)
            {
                SessionCapStatusText.Text = sessionCap.GetString() ?? "Max session cap disabled";
            }

            if (root.TryGetProperty("marketList", out var marketList) && marketList.ValueKind == JsonValueKind.String)
            {
                MarketListStatusText.Text = $"Shopping list: {marketList.GetString()}";
            }

            if (root.TryGetProperty("portfolio", out var portfolioProp) && portfolioProp.ValueKind == JsonValueKind.String)
            {
                PortfolioStatusText.Text = portfolioProp.GetString() ?? "History: —";
            }

            if (root.TryGetProperty("sessionStartedAt", out var startedProp) &&
                startedProp.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(startedProp.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var startedAt))
            {
                _sessionStartedAtUtc = startedAt;
            }

            if (root.TryGetProperty("sessionExpiresAt", out var expiresProp))
            {
                if (expiresProp.ValueKind == JsonValueKind.String &&
                    DateTimeOffset.TryParse(expiresProp.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var expiresAt))
                {
                    _sessionExpiresAtUtc = expiresAt;
                }
                else if (expiresProp.ValueKind == JsonValueKind.Null)
                {
                    _sessionExpiresAtUtc = null;
                }
            }

            UpdateSessionClockDisplay();

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
        MarketCyclePillText.Text = $"{ShortPhaseName(info.PhaseName)} · reset {(int)info.TimeUntilReset.TotalHours}h {info.TimeUntilReset.Minutes:00}m";
        if (MarketCyclePillBorder is not null)
        {
            MarketCyclePillBorder.ToolTip = $"{info.Summary}\n{info.Advice}";
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

    private string UpdateStatusCard(WorkerState state, string? detail)
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
    private void UpdateOrdersCard(string? orderBook, bool buysPaused)
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

    /// <summary>"Next AFK swap (random character) in 61m at 7:33 CH." -> "Swap in 61m".</summary>
    private static string ShortSwapHeadline(string swapText)
    {
        if (swapText.Contains("disabled", StringComparison.OrdinalIgnoreCase)) return "No AFK swap";
        if (swapText.Contains("due now", StringComparison.OrdinalIgnoreCase)) return "Swap due";
        var minutes = System.Text.RegularExpressions.Regex.Match(swapText, @"in (\d+)m");
        if (!minutes.Success || !int.TryParse(minutes.Groups[1].Value, out var total)) return "AFK swap on";
        return total >= 60 ? $"Swap in {total / 60}h {total % 60:00}m" : $"Swap in {total}m";
    }

    internal void RefreshReadinessChecklist()
    {
        var readyCount = GuidePage.RefreshChecklist();
        if (_bannerDismissed || readyCount == 4)
        {
            ReadinessBanner.Visibility = Visibility.Collapsed;
        }
        else
        {
            ReadinessBanner.Visibility = Visibility.Visible;
            ReadinessBannerText.Text = $"Setup readiness: {readyCount}/4 checks complete — open Guide & Setup for quick one-click fixes and flipping tips.";
        }
    }

    private int GetConfiguredApiTimeoutSeconds() =>
        int.TryParse(ApiTimeoutSecondsBox.Text.Trim(), out var parsed)
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
            SelectComboByTag(LaunchPlatformSelector, ApiKeyVault.ActiveEntry.LaunchPlatform);
            IsLoadingUi = wasLoading;
        }

        RebuildApiKeyViews();
    }

    internal void RebuildApiKeyViews()
    {
        var passBrush = ResolveBrush("SuccessBrush", Brushes.SeaGreen);
        var warnBrush = ResolveBrush("WarningBrush", Brushes.DarkOrange);
        var dangerBrush = ResolveBrush("DangerBrush", Brushes.IndianRed);

        var activeId = ApiKeyVault.ActiveEntry?.Id;
        _apiKeyRows.Clear();
        for (var i = 0; i < ApiKeyVault.Entries.Count; i++)
        {
            var entry = ApiKeyVault.Entries[i];
            var isActive = string.Equals(entry.Id, activeId, StringComparison.OrdinalIgnoreCase);
            var statusUpper = string.IsNullOrWhiteSpace(entry.Status) ? "UNVERIFIED" : entry.Status.ToUpperInvariant();
            var statusBrush = statusUpper switch
            {
                "VALID" => passBrush,
                "INVALID" => dangerBrush,
                _ => warnBrush
            };

            _apiKeyRows.Add(new ApiKeyRowViewModel(
                entry.Id,
                isActive,
                canMoveUp: i > 0,
                canMoveDown: i < ApiKeyVault.Entries.Count - 1,
                statusUpper,
                statusBrush,
                string.IsNullOrWhiteSpace(entry.AccountName) ? "Default Account" : entry.AccountName,
                string.IsNullOrWhiteSpace(entry.LaunchPlatform) ? "ArenaNet" : entry.LaunchPlatform,
                entry.MaskedKey,
                entry.CustomName));
        }

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

        RefreshApiKeyIndicator();
        UpdateSessionPaceSummary();
        UpdateSidebarStatusTooltip();
    }

    private async Task SwitchActiveAccountAsync(string keyId)
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
        _saveApiKeyVault(ApiKeyVault);
        HasVerifiedApiKey = ApiKeyVault.ActiveEntry?.Status.Equals("VALID", StringComparison.OrdinalIgnoreCase) == true;
        if (!string.IsNullOrWhiteSpace(ApiKeyVault.ActiveEntry?.LaunchPlatform))
        {
            var wasLoading = IsLoadingUi;
            IsLoadingUi = true;
            SelectComboByTag(LaunchPlatformSelector, ApiKeyVault.ActiveEntry.LaunchPlatform);
            IsLoadingUi = wasLoading;
        }

        RebuildApiKeyViews();
        RefreshReadinessChecklist();
        SaveAllSettingsSilently();

        var activeLabel = ApiKeyVault.ActiveEntry?.DisplayLabel ?? "Account";
        AddLog($"Switched active account to {activeLabel}.");
        await IncomePage.ReloadLedgerForActiveAccountAsync();
        await ReportsPage.LoadSessionReportsAsync();

        if (_supervisor is DesktopWorkerSupervisor concreteSupervisor && _lastWorkerState is WorkerState.Idle or WorkerState.Stopped)
        {
            await concreteSupervisor.RestartWorkerAsync();
        }
    }

    private void MoveApiKeyEntry(string keyId, int delta)
    {
        var list = ApiKeyVault.Entries.ToList();
        var index = list.FindIndex(item => string.Equals(item.Id, keyId, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return;
        var targetIndex = index + delta;
        if (targetIndex < 0 || targetIndex >= list.Count) return;

        (list[index], list[targetIndex]) = (list[targetIndex], list[index]);
        ApiKeyVault = ApiKeyVault with { Entries = list.ToArray() };
        _saveApiKeyVault(ApiKeyVault);
        RebuildApiKeyViews();
    }

    internal void UpdateVaultEntry(string keyId, Func<Gw2ApiKeyEntry, Gw2ApiKeyEntry> transform)
    {
        var updated = ApiKeyVault.Entries
            .Select(item => string.Equals(item.Id, keyId, StringComparison.OrdinalIgnoreCase) ? transform(item) : item)
            .ToArray();
        ApiKeyVault = ApiKeyVault with { Entries = updated };
        _saveApiKeyVault(ApiKeyVault);
    }

    private async Task RemoveVaultEntryAsync(string keyId)
    {
        var remaining = ApiKeyVault.Entries
            .Where(item => !string.Equals(item.Id, keyId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var nextActiveId = string.Equals(ApiKeyVault.ActiveKeyId, keyId, StringComparison.OrdinalIgnoreCase)
            ? remaining.FirstOrDefault()?.Id
            : ApiKeyVault.ActiveKeyId;

        ApiKeyVault = new Gw2ApiKeyVaultData(nextActiveId, remaining);
        _saveApiKeyVault(ApiKeyVault);
        HasVerifiedApiKey = ApiKeyVault.ActiveEntry?.Status.Equals("VALID", StringComparison.OrdinalIgnoreCase) == true;
        RebuildApiKeyViews();
        RefreshReadinessChecklist();
        AddLog("Removed API key from DPAPI vault.");

        await IncomePage.ReloadLedgerForActiveAccountAsync();
        await ReportsPage.LoadSessionReportsAsync();
        if (_supervisor is DesktopWorkerSupervisor concreteSupervisor && _lastWorkerState is WorkerState.Idle or WorkerState.Stopped)
        {
            await concreteSupervisor.RestartWorkerAsync();
        }
    }

    private void RegisterConfiguredHotkeys(bool logWarnings)
    {
        var pauseKey = NullIfBlank(PauseHotkeyBox.Text);
        var stopKey = NullIfBlank(StopHotkeyBox.Text);
        var emergencyKey = NullIfBlank(EmergencyStopHotkeyBox.Text);
        var warnings = _hotkeys.RegisterSet(
            pauseKey,
            () => _ = Dispatcher.InvokeAsync(async () =>
            {
                var cmd = _lastWorkerState == WorkerState.Paused ? WorkerCommandTypes.Resume : WorkerCommandTypes.Pause;
                await SendAsync(new WorkerCommand(cmd));
            }),
            stopKey,
            () => _ = Dispatcher.InvokeAsync(async () => await SendAsync(new WorkerCommand(WorkerCommandTypes.Stop))),
            emergencyKey,
            () => _ = Dispatcher.InvokeAsync(async () => await SendAsync(new WorkerCommand(WorkerCommandTypes.EmergencyStop))));

        SidebarHotkeyHint.Text = $"Pause: {pauseKey ?? "off"}  •  Stop: {stopKey ?? "off"}";
        if (warnings.Count == 0)
        {
            HotkeyStatusText.Text = $"Active hotkeys — Pause/Resume: {pauseKey ?? "off"}, Stop: {stopKey ?? "off"}, Emergency: {emergencyKey ?? "off"}.";
        }
        else
        {
            HotkeyStatusText.Text = string.Join(" ", warnings);
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

    private void UpdateSidebarStatusTooltip()
    {
        if (SidebarStatusBorder is null || SidebarStatusLabel is null)
        {
            return;
        }

        var statusLabel = SidebarStatusLabel.Text ?? "Ready";
        var clockText = SessionClockText?.Text ?? "00:00:00";
        var accountLabel = ApiKeyVault.ActiveEntry?.DisplayLabel ?? "None";
        var hotkeys = SidebarHotkeyHint?.Text ?? string.Empty;
        SidebarStatusBorder.ToolTip = string.IsNullOrWhiteSpace(hotkeys)
            ? $"Status: {statusLabel}\nClock: {clockText}\nAccount: {accountLabel}"
            : $"Status: {statusLabel}\nClock: {clockText}\nAccount: {accountLabel}\n{hotkeys}";
    }

    internal void UpdateSessionClockDisplay()
    {
        if (SessionClockText is null || SessionClockIcon is null || SessionClockPillBorder is null)
        {
            return;
        }

        if (_sessionStartedAtUtc is not { } startedAt)
        {
            SessionClockText.Text = "00:00:00";
            SessionClockIcon.Foreground = ResolveBrush("AccentBrush", Brushes.DodgerBlue);
            SessionClockPillBorder.ToolTip = "Elapsed trade session clock (starts with each session).";
            UpdateSidebarStatusTooltip();
            return;
        }

        var elapsed = DateTimeOffset.UtcNow - startedAt;
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        var hours = (int)elapsed.TotalHours;
        SessionClockText.Text = $"{hours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";

        if (_sessionExpiresAtUtc is { } expiresAt)
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

        UpdateSidebarStatusTooltip();
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

    private void RefreshApiKeyIndicator()
    {
        if (ApiKeyVault.ActiveEntry is { } active)
        {
            var countNote = ApiKeyVault.Entries.Count > 1 ? $" ({ApiKeyVault.Entries.Count} accounts saved)" : string.Empty;
            ApiKeyStatusText.Text = $"Active account: {active.DisplayLabel} [{active.Status}]{countNote}. Stored securely via Windows DPAPI.";
        }
        else if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MMOPAL_Native__Gw2ApiKey")))
        {
            ApiKeyStatusText.Text = "Using GW2 API key from MMOPAL_Native__Gw2ApiKey environment variable.";
        }
        else
        {
            ApiKeyStatusText.Text = "No API key stored yet. Required scopes: account, tradingpost, wallet, inventories.";
        }
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

    private void LoadLimits()
    {
        var limits = _loadLimits();
        MinimumProfitBox.Text = limits.MinimumProfitPercentage == 0
            ? string.Empty
            : limits.MinimumProfitPercentage.ToString(CultureInfo.CurrentCulture);
        MaximumBuyGoldBox.Text = limits.MaximumBuyPriceGold?.ToString(CultureInfo.CurrentCulture) ?? string.Empty;
    }

    private void LoadClientSettings()
    {
        var settings = _loadClientSettings();
        var installs = _discoverGw2Installs();
        var detectedPath = installs.FirstOrDefault()?.ExecutablePath ?? _detectGw2Path();
        var inferredPlatform = !string.IsNullOrWhiteSpace(settings.LaunchPlatform)
            ? settings.LaunchPlatform
            : settings.IsSteamPlatform ? Gw2ClientSettings.PlatformSteam : Gw2ClientSettings.PlatformArenaNet;
        SelectComboByTag(LaunchPlatformSelector, inferredPlatform);

        Gw2ExecutablePathBox.Text = string.IsNullOrWhiteSpace(settings.ExecutablePath)
            ? detectedPath ?? string.Empty
            : settings.ExecutablePath;
        Gw2LaunchArgsBox.Text = string.IsNullOrWhiteSpace(settings.LaunchArguments) ||
                                string.Equals(settings.LaunchArguments.Trim(), "-windowed", StringComparison.OrdinalIgnoreCase)
            ? "-autologin -windowed"
            : settings.LaunchArguments;
        Gw2WidthBox.Text = settings.WindowWidth > 0 ? settings.WindowWidth.ToString(CultureInfo.InvariantCulture) : "1024";
        Gw2HeightBox.Text = settings.WindowHeight > 0 ? settings.WindowHeight.ToString(CultureInfo.InvariantCulture) : "768";
        MaxLaunchAttemptsBox.Text = Math.Clamp(settings.MaxLaunchAttempts, 1, 5).ToString(CultureInfo.InvariantCulture);
        WindowWaitSecondsBox.Text = Math.Clamp(settings.WindowWaitTimeoutSeconds, 5, 180).ToString(CultureInfo.InvariantCulture);
        MaxRecoveryRetriesBox.Text = Math.Clamp(settings.MaxRecoveryRetries, 0, 10).ToString(CultureInfo.InvariantCulture);
        MaxGameRestartsBox.Text = Math.Clamp(settings.MaxGameRestartsPerSession, 0, 5).ToString(CultureInfo.InvariantCulture);
        AutoRestartOnFailureCheckBox.IsChecked = settings.AutoRestartGameOnFailure;
        MaxSessionHoursBox.Text = settings.FormatMaxSessionHours();
        AutoCloseGameOnCapCheckBox.IsChecked = settings.AutoCloseGameOnSessionCap;
        AutoCloseAppOnCapCheckBox.IsChecked = settings.AutoCloseAppOnSessionCap;
        ShutdownPcOnCapCheckBox.IsChecked = settings.ShutdownPcOnSessionCap;
        PcShutdownCountdownBox.Text = Math.Clamp(settings.PcShutdownCountdownSeconds, 10, 600).ToString(CultureInfo.InvariantCulture);
        ParkedCharactersBox.Text = Math.Max(1, settings.ParkedCharacterCount).ToString(CultureInfo.InvariantCulture);
        InteractKeyBox.Text = string.IsNullOrWhiteSpace(settings.InteractKeybind) ? "F" : settings.InteractKeybind;
        TpKeyBox.Text = string.IsNullOrWhiteSpace(settings.TradingPostKeybind) ? "O" : settings.TradingPostKeybind;
        RelistStaleSellsCheckBox.IsChecked = settings.RelistStaleUndercutSells;
        MaxBuyOrderShareBox.Text = Math.Clamp(settings.MaxBuyOrderSharePercent, 0, 100).ToString(CultureInfo.InvariantCulture);
        SwapCharacterHoursBox.Text = settings.FormatSwapHours();
        SwapJitterMinutesBox.Text = settings.SwapCharacterJitterMinutes.ToString(CultureInfo.InvariantCulture);
        SnapWindowOnStartCheckBox.IsChecked = settings.AutoPrepareOnScreenStart;
        AutoLaunchGw2CheckBox.IsChecked = settings.AutoLaunchIfNotRunning;
        RandomCharacterSwapCheckBox.IsChecked = settings.RandomizeCharacterSelection;

        var latencyId = string.IsNullOrWhiteSpace(settings.LatencyProfile) ? "HighPing" : settings.LatencyProfile;
        SelectComboByTag(LatencyProfileSelector, latencyId);
        SelectComboByTag(PriceEntryModeSelector, settings.TypesExactPrice ? Gw2ClientSettings.PriceEntryTypeExact : Gw2ClientSettings.PriceEntryClickRow);
        UiLoadWaitMsBox.Text = Math.Clamp(settings.UiLoadWaitMs, 500, 30_000).ToString(CultureInfo.InvariantCulture);
        ActionSettleDelayMsBox.Text = Math.Clamp(settings.ActionSettleDelayMs, 100, 5_000).ToString(CultureInfo.InvariantCulture);
        ApiTimeoutSecondsBox.Text = Math.Clamp(settings.ApiTimeoutSeconds, 15, 120).ToString(CultureInfo.InvariantCulture);
        if (Gw2LatencyPresets.FindById(latencyId) is { } latencyPreset)
        {
            LatencyDescriptionText.Text = latencyPreset.Description;
        }

        var dpi = DetectDisplayScale();
        var installInfo = installs.FirstOrDefault() is { } first
            ? $"{first.Kind} ({first.Source})"
            : !string.IsNullOrWhiteSpace(Gw2ExecutablePathBox.Text) ? "Configured path" : "Not detected yet";
        Gw2DetectHealthText.Text = $"Client: {installInfo}  •  Display Scale: {dpi.Summary}";
        SessionCapStatusText.Text = settings.MaxSessionDurationMinutes is > 0
            ? $"Session cap: {settings.FormatMaxSessionHours()}h"
            : "Session cap: disabled";
    }

    private void LoadOperatorSettings()
    {
        var settings = _loadOperatorSettings();
        SelectComboByContent(ThemeSelector, settings.ThemeMode);
        if (Application.Current is App app)
        {
            app.ApplyTheme(settings.ThemeMode);
        }

        PauseHotkeyBox.Text = settings.PauseHotkey;
        StopHotkeyBox.Text = settings.StopHotkey;
        EmergencyStopHotkeyBox.Text = settings.EmergencyStopHotkey;
        OverbidMinBox.Text = settings.OverbidBufferMinCopper.ToString(CultureInfo.InvariantCulture);
        OverbidMaxBox.Text = settings.OverbidBufferMaxCopper.ToString(CultureInfo.InvariantCulture);
        UndercutMinBox.Text = settings.UndercutBufferMinCopper.ToString(CultureInfo.InvariantCulture);
        UndercutMaxBox.Text = settings.UndercutBufferMaxCopper.ToString(CultureInfo.InvariantCulture);
        DefaultQuantityBox.Text = Math.Max(1, settings.DefaultOrderQuantity).ToString(CultureInfo.InvariantCulture);
        GoldThresholdNoBuyBox.Text = settings.GoldThresholdNoBuy?.ToString(CultureInfo.CurrentCulture) ?? string.Empty;
        UseCustomListOnStartCheckBox.IsChecked = settings.UseCustomShoppingList;
        CandidatesPathBox.Text = settings.CandidatesPath ?? string.Empty;
        BlacklistPathBox.Text = settings.BlacklistPath ?? string.Empty;
        FixturePathBox.Text = settings.FixturePath ?? string.Empty;
        RecordPathBox.Text = settings.RecordPath ?? string.Empty;
        CaptureFramesCheckBox.IsChecked = settings.CaptureFrames;
        ScannerPage.ScanVelocityCheckBox.IsChecked = settings.EnableDataWarsVelocity;
        AutoSellInventoryCheckBox.IsChecked = settings.AutoListBoughtFlipItems;
        ScannerPage.AutoRescanHoursBox.Text = settings.AutoRescanHours.ToString(CultureInfo.CurrentCulture);
        ScannerPage.ApplyScannerUiState(settings.Scanner);
        AutomateOnLaunchCheckBox.IsChecked = settings.AutomateOnLaunch;
        AutoTuneEachSessionCheckBox.IsChecked = settings.AutoTuneEachSession;
        _autoTuneKeeps.Clear();
        foreach (var key in settings.AutoTuneKeeps ?? Array.Empty<string>()) _autoTuneKeeps.Add(key);
        UpdateAutoTuneKeepsHint();
        _lastAutoTunedPreset = settings.LastAutoTunedPreset;
        AutoDockBesideGw2CheckBox.IsChecked = settings.AutoDockSideBySideWhenRunning;
        UsePortfolioIntelligenceCheckBox.IsChecked = settings.UsePortfolioIntelligence;
        SaveSessionReportsCheckBox.IsChecked = settings.SaveSessionReports;
        ReportIncludeDryRunCheckBox.IsChecked = settings.ReportIncludeDryRunAndObserve;
        ReportSaveOnTradesCheckBox.IsChecked = settings.ReportAlwaysSaveIfTradesOccurred;
        ReportSaveOnErrorsCheckBox.IsChecked = settings.ReportAlwaysSaveOnErrors;
        ReportMinDurationBox.Text = Math.Clamp(settings.ReportMinDurationMinutes, 0, 1440).ToString(CultureInfo.InvariantCulture);
        MaxSavedReportsBox.Text = Math.Clamp(settings.MaxSavedSessionReports, 10, 1000).ToString(CultureInfo.InvariantCulture);
        ReportsPage.UpdateReportRulesSummary(settings.ToSessionReportPolicy());
    }

    private bool TryReadLimits(out TradingLimits limits)
    {
        if (TradingLimits.TryParse(MinimumProfitBox.Text, MaximumBuyGoldBox.Text, out limits, out var error))
        {
            return true;
        }

        StatusText.Text = error ?? "Trading limits are invalid.";
        return false;
    }

    private bool TryReadClientSettings(out Gw2ClientSettings settings)
    {
        settings = new Gw2ClientSettings();
        if (!Gw2ClientSettings.TryParseSwapHours(SwapCharacterHoursBox.Text, out var swapMinutes, out var error))
        {
            StatusText.Text = error ?? "Character swap interval is invalid.";
            return false;
        }

        if (!Gw2ClientSettings.TryParseMaxSessionHours(MaxSessionHoursBox.Text, out var maxSessionMinutes, out var capError))
        {
            StatusText.Text = capError ?? "Max session duration is invalid.";
            return false;
        }

        var width = int.TryParse(Gw2WidthBox.Text.Trim(), out var parsedW) && parsedW >= 640 ? parsedW : 1024;
        var height = int.TryParse(Gw2HeightBox.Text.Trim(), out var parsedH) && parsedH >= 480 ? parsedH : 768;
        var jitter = int.TryParse(SwapJitterMinutesBox.Text.Trim(), out var parsedJ) && parsedJ >= 0 ? parsedJ : 15;
        var maxAttempts = int.TryParse(MaxLaunchAttemptsBox.Text.Trim(), out var parsedAtt) ? Math.Clamp(parsedAtt, 1, 5) : 2;
        var waitSeconds = int.TryParse(WindowWaitSecondsBox.Text.Trim(), out var parsedWait) ? Math.Clamp(parsedWait, 5, 180) : 45;
        var parkedChars = int.TryParse(ParkedCharactersBox.Text.Trim(), out var parsedChars) ? Math.Clamp(parsedChars, 1, 64) : 4;
        var interactKey = NullIfBlank(InteractKeyBox.Text) ?? "F";
        var tpKey = NullIfBlank(TpKeyBox.Text) ?? "O";
        var latencyProfile = (LatencyProfileSelector.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "HighPing";
        var uiLoadWaitMs = int.TryParse(UiLoadWaitMsBox.Text.Trim(), out var parsedUiWait)
            ? Math.Clamp(parsedUiWait, 500, 30_000)
            : 7500;
        var actionSettleMs = int.TryParse(ActionSettleDelayMsBox.Text.Trim(), out var parsedSettle)
            ? Math.Clamp(parsedSettle, 100, 5_000)
            : 900;
        var apiTimeoutSeconds = int.TryParse(ApiTimeoutSecondsBox.Text.Trim(), out var parsedTimeout)
            ? Math.Clamp(parsedTimeout, 15, 120)
            : 60;
        var launchPlatform = GetSelectedLaunchPlatform();
        var maxRecoveryRetries = int.TryParse(MaxRecoveryRetriesBox.Text.Trim(), out var parsedRec)
            ? Math.Clamp(parsedRec, 0, 10)
            : 3;
        var maxGameRestarts = int.TryParse(MaxGameRestartsBox.Text.Trim(), out var parsedRestarts)
            ? Math.Clamp(parsedRestarts, 0, 5)
            : 2;
        var autoRestartOnFailure = AutoRestartOnFailureCheckBox.IsChecked == true;
        var shutdownCountdownSeconds = int.TryParse(PcShutdownCountdownBox.Text.Trim(), out var parsedCd)
            ? Math.Clamp(parsedCd, 10, 600)
            : 60;

        settings = new Gw2ClientSettings(
            ExecutablePath: NullIfBlank(Gw2ExecutablePathBox.Text),
            LaunchArguments: NullIfBlank(Gw2LaunchArgsBox.Text) ?? "-autologin -windowed",
            WindowWidth: width,
            WindowHeight: height,
            AutoPrepareOnScreenStart: SnapWindowOnStartCheckBox.IsChecked == true,
            AutoLaunchIfNotRunning: AutoLaunchGw2CheckBox.IsChecked == true,
            SwapCharacterIntervalMinutes: swapMinutes,
            SwapCharacterJitterMinutes: jitter,
            RandomizeCharacterSelection: RandomCharacterSwapCheckBox.IsChecked == true,
            MaxLaunchAttempts: maxAttempts,
            WindowWaitTimeoutSeconds: waitSeconds,
            MaxSessionDurationMinutes: maxSessionMinutes,
            ParkedCharacterCount: parkedChars,
            InteractKeybind: interactKey,
            TradingPostKeybind: tpKey,
            RelistStaleUndercutSells: RelistStaleSellsCheckBox.IsChecked == true,
            MaxBuyOrderSharePercent: int.TryParse(MaxBuyOrderShareBox.Text.Trim(), out var buyShare) ? Math.Clamp(buyShare, 0, 100) : 50,
            LatencyProfile: latencyProfile,
            UiLoadWaitMs: uiLoadWaitMs,
            ActionSettleDelayMs: actionSettleMs,
            ApiTimeoutSeconds: apiTimeoutSeconds,
            LaunchPlatform: launchPlatform,
            MaxRecoveryRetries: maxRecoveryRetries,
            AutoRestartGameOnFailure: autoRestartOnFailure,
            MaxGameRestartsPerSession: maxGameRestarts,
            AutoCloseGameOnSessionCap: AutoCloseGameOnCapCheckBox.IsChecked == true,
            AutoCloseAppOnSessionCap: AutoCloseAppOnCapCheckBox.IsChecked == true,
            ShutdownPcOnSessionCap: ShutdownPcOnCapCheckBox.IsChecked == true,
            PcShutdownCountdownSeconds: shutdownCountdownSeconds,
            PriceEntryMode: (PriceEntryModeSelector.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? Gw2ClientSettings.PriceEntryTypeExact);
        SessionCapStatusText.Text = settings.MaxSessionDurationMinutes is > 0
            ? $"Session cap: {settings.FormatMaxSessionHours()}h"
            : "Session cap: disabled";
        return true;
    }

    private bool TryReadOperatorSettings(out OperatorSettings settings)
    {
        var theme = (ThemeSelector.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "System";
        var overbidMin = int.TryParse(OverbidMinBox.Text.Trim(), out var obMin) && obMin >= 0 ? obMin : 1;
        var overbidMax = int.TryParse(OverbidMaxBox.Text.Trim(), out var obMax) && obMax >= overbidMin ? obMax : overbidMin;
        var undercutMin = int.TryParse(UndercutMinBox.Text.Trim(), out var ucMin) && ucMin >= 0 ? ucMin : 1;
        var undercutMax = int.TryParse(UndercutMaxBox.Text.Trim(), out var ucMax) && ucMax >= undercutMin ? ucMax : undercutMin;
        var defaultQty = long.TryParse(DefaultQuantityBox.Text.Trim(), out var qty) && qty > 0 ? qty : 1;
        decimal? goldFloor = decimal.TryParse(GoldThresholdNoBuyBox.Text.Trim(), NumberStyles.Number, CultureInfo.CurrentCulture, out var parsedFloor) && parsedFloor >= 0
            ? parsedFloor
            : null;
        var reportMinDuration = int.TryParse(ReportMinDurationBox.Text.Trim(), out var parsedMinDur)
            ? Math.Clamp(parsedMinDur, 0, 1440)
            : 5;
        var maxSavedReports = int.TryParse(MaxSavedReportsBox.Text.Trim(), out var parsedMaxRpts)
            ? Math.Clamp(parsedMaxRpts, 10, 1000)
            : 100;

        settings = new OperatorSettings(
            ThemeMode: theme,
            PauseHotkey: NullIfBlank(PauseHotkeyBox.Text) ?? string.Empty,
            StopHotkey: NullIfBlank(StopHotkeyBox.Text) ?? string.Empty,
            EmergencyStopHotkey: NullIfBlank(EmergencyStopHotkeyBox.Text) ?? string.Empty,
            OverbidBufferMinCopper: overbidMin,
            OverbidBufferMaxCopper: overbidMax,
            UndercutBufferMinCopper: undercutMin,
            UndercutBufferMaxCopper: undercutMax,
            DefaultOrderQuantity: defaultQty,
            GoldThresholdNoBuy: goldFloor,
            UseCustomShoppingList: UseCustomListOnStartCheckBox.IsChecked == true,
            CandidatesPath: NullIfBlank(CandidatesPathBox.Text),
            BlacklistPath: NullIfBlank(BlacklistPathBox.Text),
            FixturePath: NullIfBlank(FixturePathBox.Text),
            RecordPath: NullIfBlank(RecordPathBox.Text),
            CaptureFrames: CaptureFramesCheckBox.IsChecked == true,
            EnableDataWarsVelocity: ScannerPage.ScanVelocityCheckBox.IsChecked == true,
            AutoListBoughtFlipItems: AutoSellInventoryCheckBox.IsChecked == true,
            AutoRescanHours: ScannerPage.ReadAutoRescanHours(),
            AutoDockSideBySideWhenRunning: AutoDockBesideGw2CheckBox.IsChecked == true,
            UsePortfolioIntelligence: UsePortfolioIntelligenceCheckBox.IsChecked == true,
            SaveSessionReports: SaveSessionReportsCheckBox.IsChecked == true,
            ReportIncludeDryRunAndObserve: ReportIncludeDryRunCheckBox.IsChecked == true,
            ReportMinDurationMinutes: reportMinDuration,
            ReportAlwaysSaveIfTradesOccurred: ReportSaveOnTradesCheckBox.IsChecked == true,
            ReportAlwaysSaveOnErrors: ReportSaveOnErrorsCheckBox.IsChecked == true,
            MaxSavedSessionReports: maxSavedReports,
            Scanner: ScannerPage.CaptureScannerUiState(),
            AutomateOnLaunch: AutomateOnLaunchCheckBox.IsChecked == true,
            LastAutoTunedPreset: _lastAutoTunedPreset,
            AutoTuneEachSession: AutoTuneEachSessionCheckBox.IsChecked == true,
            AutoTuneKeeps: _autoTuneKeeps.Count == 0 ? null : _autoTuneKeeps.OrderBy(key => key, StringComparer.Ordinal).ToArray());
        ReportsPage.UpdateReportRulesSummary(settings.ToSessionReportPolicy());
        return true;
    }

    internal void SaveOperatorSettingsIfValid()
    {
        if (TryReadOperatorSettings(out var opSettings))
        {
            _saveOperatorSettings(opSettings);
        }
    }

    private void SaveAllSettingsSilently()
    {
        if (TradingLimits.TryParse(MinimumProfitBox.Text, MaximumBuyGoldBox.Text, out var limits, out _))
        {
            _saveLimits(limits);
        }

        if (TryReadClientSettings(out var clientSettings))
        {
            _saveClientSettings(clientSettings);
        }

        SaveOperatorSettingsIfValid();
    }

    private sealed class ApiKeyRowViewModel(
        string id,
        bool isActive,
        bool canMoveUp,
        bool canMoveDown,
        string statusText,
        Brush statusBrush,
        string accountName,
        string platformLabel,
        string maskedKey,
        string customName)
    {
        public string Id { get; } = id;
        public bool IsActive { get; } = isActive;
        public string UseLabel => IsActive ? "Active" : "Use";
        public bool CanUse => !IsActive;
        public bool CanMoveUp { get; } = canMoveUp;
        public bool CanMoveDown { get; } = canMoveDown;
        public string StatusText { get; } = statusText;
        public Brush StatusBrush { get; } = statusBrush;
        public string AccountName { get; } = accountName;
        public string PlatformLabel { get; } = platformLabel;
        public string MaskedKey { get; } = maskedKey;
        public string CustomName { get; set; } = customName;
    }

    // ---------------------------------------------------------------------
    // Max Cap Completion Countdown
    // ---------------------------------------------------------------------

    internal void HandleSessionCapCompletion(SessionReportNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        if (string.Equals(_lastHandledCapReportId, notification.Report.Id, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _lastHandledCapReportId = notification.Report.Id;
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
