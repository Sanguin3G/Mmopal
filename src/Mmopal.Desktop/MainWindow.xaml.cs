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
    private readonly Func<DisplayScaleInfo> _detectDisplayScale;
    private readonly TradingLedgerStore? _ledgerStore;
    private readonly SessionReportStore? _sessionReportStore;
    private readonly IGw2MarketScanner _marketScanner;
    private readonly Func<Gw2ApiKeyVaultData> _loadApiKeyVault;
    private readonly Action<Gw2ApiKeyVaultData> _saveApiKeyVault;
    private readonly Action _requestAppClose;
    private readonly Action<int> _executeSystemShutdown;
    private readonly GlobalHotkeyService _hotkeys = new();
    private readonly HttpClient _gw2HttpClient = new() { BaseAddress = new Uri("https://api.guildwars2.com/"), Timeout = TimeSpan.FromSeconds(120) };
    private readonly DispatcherTimer _cycleTimer;
    private readonly DispatcherTimer _sessionClockTimer;

    private readonly ObservableCollection<MarketScanItem> _scanResults = new();
    private readonly ObservableCollection<ItemCandidate> _customShoppingList = new();
    private readonly ObservableCollection<string> _blacklist = new();
    private readonly ObservableCollection<ItemPnlRow> _itemPnlRows = new();
    private readonly ObservableCollection<ActiveOrderViewRow> _activeOrderRows = new();
    private readonly ObservableCollection<LedgerHistoryViewRow> _ledgerRows = new();
    private readonly ObservableCollection<ChartBarItem> _dailyChartBars = new();
    private readonly ObservableCollection<ChartBarItem> _hourlyChartBars = new();
    private readonly ObservableCollection<ApiKeyRowViewModel> _apiKeyRows = new();
    private readonly ObservableCollection<SessionReportListItem> _sessionReportRows = new();
    private readonly ObservableCollection<ReportTradeRow> _reportTradeRows = new();
    private readonly ObservableCollection<ReportIncidentRow> _reportIncidentRows = new();
    private readonly List<Gw2ActiveOrder> _latestActiveOrders = new();
    private readonly List<(DateTime Timestamp, string Human, string Raw)> _logEntries = new();

    private Gw2ApiKeyVaultData _apiKeyVault = new(null, Array.Empty<Gw2ApiKeyEntry>());
    private IReadOnlyList<Gw2LedgerTransaction> _archivedTransactions = Array.Empty<Gw2LedgerTransaction>();
    private IReadOnlyList<ItemPnlRow> _allTimeframePnlRows = Array.Empty<ItemPnlRow>();
    private IReadOnlyList<TradingSessionReport> _allLoadedReports = Array.Empty<TradingSessionReport>();
    private TradingSessionReport? _selectedReport;
    private TradingIncomeReport? _lastIncomeReport;
    private Gw2EquitySnapshot? _latestEquitySnapshot;
    private Gw2DeliverySnapshot? _latestDelivery;
    private int _openBuyCount;
    private int _openSellCount;
    private int _outbidBuyCount;
    private int _undercutSellCount;
    private bool _bannerDismissed;
    private bool _hasVerifiedApiKey;
    private Rect? _preDockBounds;
    private WindowState? _preDockWindowState;
    private bool _isSideDocked;
    private DateTimeOffset? _sessionStartedAtUtc;
    private DateTimeOffset? _sessionExpiresAtUtc;
    private string? _lastHandledCapReportId;

    private CancellationTokenSource? _scanCancellation;
    private WorkerState _lastWorkerState = WorkerState.Idle;
    private bool _loadingUi;

    public MainWindow()
        : this(
            new DesktopWorkerSupervisor(),
            () => TradingLimitsFile.LoadOrDefault(LimitsPath),
            limits => TradingLimitsFile.Save(LimitsPath, limits),
            () => Gw2ClientSettingsFile.LoadOrDefault(ClientSettingsPath),
            settings => Gw2ClientSettingsFile.Save(ClientSettingsPath, settings),
            new Gw2ClientPreparer(),
            Gw2InstallationLocator.DetectDefaultExecutablePath,
            () => OperatorSettingsFile.LoadOrDefault(OperatorSettingsPath),
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
        _detectDisplayScale = detectDisplayScale ?? (() => new DisplayScaleInfo(96, 100, true, "100% (96 DPI)"));
        _ledgerStore = ledgerStore;
        _sessionReportStore = sessionReportStore;
        _marketScanner = marketScanner ?? new Gw2MarketScanner(_gw2HttpClient, _ledgerStore);
        _loadApiKeyVault = loadApiKeyVault ?? (() => new Gw2ApiKeyVaultData(null, Array.Empty<Gw2ApiKeyEntry>()));
        _saveApiKeyVault = saveApiKeyVault ?? (_ => { });
        _requestAppClose = requestAppClose ?? Close;
        _executeSystemShutdown = executeSystemShutdown ?? TriggerWindowsSystemShutdown;

        _loadingUi = true;
        _cycleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _cycleTimer.Tick += (_, _) => UpdateMarketCycleDisplay();
        _sessionClockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _sessionClockTimer.Tick += (_, _) => UpdateSessionClockDisplay();

        InitializeComponent();
        ScanResultsGrid.ItemsSource = _scanResults;
        CustomShoppingListGrid.ItemsSource = _customShoppingList;
        BlacklistListBox.ItemsSource = _blacklist;
        ItemPnlGrid.ItemsSource = _itemPnlRows;
        ActiveOrdersGrid.ItemsSource = _activeOrderRows;
        LedgerHistoryGrid.ItemsSource = _ledgerRows;
        DailyProfitChartItems.ItemsSource = _dailyChartBars;
        HourlyActivityChartItems.ItemsSource = _hourlyChartBars;
        ApiKeysGrid.ItemsSource = _apiKeyRows;
        SessionReportsListBox.ItemsSource = _sessionReportRows;
        ReportTradesGrid.ItemsSource = _reportTradeRows;
        ReportIncidentsGrid.ItemsSource = _reportIncidentRows;
        ShowRawLogCheckBox.Checked += (_, _) => RebuildLogList();
        ShowRawLogCheckBox.Unchecked += (_, _) => RebuildLogList();

        LoadLimits();
        LoadClientSettings();
        LoadOperatorSettings();
        LoadOperatorLists(
            loadCustomList ?? (() => Array.Empty<ItemCandidate>()),
            loadBlacklist ?? (() => Array.Empty<string>()));
        LoadApiKeyVault();
        UpdateMarketCycleDisplay();
        UpdateSessionPaceSummary();
        RefreshReadinessChecklist();
        UpdateSessionButtonStates(WorkerState.Idle);
        UpdateSafeModePill();
        UpdateSidebarStatus(WorkerState.Idle, "Ready (Offline)");
        _loadingUi = false;

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
            await LoadCachedPortfolioOnStartupAsync();
            await LoadSessionReportsAsync();
        };
        Closed += (_, _) =>
        {
            _cycleTimer.Stop();
            _sessionClockTimer.Stop();
            _scanCancellation?.Cancel();
            _scanCancellation?.Dispose();
            _hotkeys.Dispose();
            SaveAllSettingsSilently();
            _gw2HttpClient.Dispose();
            var disposeTask = Task.Run(async () => await _supervisor.DisposeAsync().ConfigureAwait(false));
            disposeTask.Wait(TimeSpan.FromSeconds(2));
        };
    }

    // ---------------------------------------------------------------------
    // Navigation & Theme
    // ---------------------------------------------------------------------

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (SessionView is null || IncomeView is null || ReportsView is null || ScannerView is null ||
            ListsView is null || SettingsView is null || GuideView is null)
        {
            return;
        }

        SessionView.Visibility = NavSession.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        IncomeView.Visibility = NavIncome.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ReportsView.Visibility = NavReports.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ScannerView.Visibility = NavScanner.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ListsView.Visibility = NavLists.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SettingsView.Visibility = NavSettings.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        GuideView.Visibility = NavGuide.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        if (NavReports.IsChecked == true)
        {
            _ = LoadSessionReportsAsync();
        }
    }

    private void OpenGuide_Click(object sender, RoutedEventArgs e) => NavGuide.IsChecked = true;
    private void GoToSettings_Click(object sender, RoutedEventArgs e) => NavSettings.IsChecked = true;
    private void GoToScanner_Click(object sender, RoutedEventArgs e) => NavScanner.IsChecked = true;

    private void DismissReadinessBanner_Click(object sender, RoutedEventArgs e)
    {
        _bannerDismissed = true;
        ReadinessBanner.Visibility = Visibility.Collapsed;
    }

    private void RefreshChecklist_Click(object sender, RoutedEventArgs e) => RefreshReadinessChecklist();

    private void ThemeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingUi || ThemeSelector.SelectedItem is not ComboBoxItem item) return;
        var mode = item.Content.ToString()!;
        if (Application.Current is App app)
        {
            app.ApplyTheme(mode);
            app.ApplyWindowTitleBar(this);
        }

        if (_lastIncomeReport is not null)
        {
            RebuildIncomeCharts(_lastIncomeReport);
        }

        if (_allLoadedReports.Count > 0)
        {
            ApplyReportsFilter();
        }

        RefreshReadinessChecklist();
        SaveOperatorSettingsIfValid();
    }

    // ---------------------------------------------------------------------
    // Session Controls
    // ---------------------------------------------------------------------

    private void ModeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingUi) return;

        // Live trading acts on the real game, so it always reads the real game screen.
        if (IsSelectedTag(ModeSelector, nameof(RunMode.Live)) && SourceSelector is not null && !IsSelectedTag(SourceSelector, "screen"))
        {
            SelectByTag(SourceSelector, "screen");
        }

        UpdateSafeModePill();
    }

    private void SourceSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingUi || ModeSelector is null) return;

        // Replay data is a recorded sample; arming live clicks against it makes no sense, so fall back to Dry run.
        if (IsSelectedTag(SourceSelector, "replay") && IsSelectedTag(ModeSelector, nameof(RunMode.Live)))
        {
            SelectByTag(ModeSelector, nameof(RunMode.DryRun));
        }
    }

    private static bool IsSelectedTag(ComboBox? combo, string tag) =>
        string.Equals((combo?.SelectedItem as ComboBoxItem)?.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase);

    private static void SelectByTag(ComboBox combo, string tag)
    {
        foreach (var item in combo.Items)
        {
            if (item is ComboBoxItem comboItem && string.Equals(comboItem.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = comboItem;
                return;
            }
        }
    }

    private void ProfileSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingUi) return;
        UpdateSessionPaceSummary();
    }

    private void LaunchPlatformSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingUi) return;
        var platformTag = GetSelectedLaunchPlatform();
        if (_apiKeyVault.ActiveEntry is { } active &&
            !string.Equals(active.LaunchPlatform, platformTag, StringComparison.OrdinalIgnoreCase))
        {
            UpdateVaultEntry(active.Id, entry => entry with { LaunchPlatform = platformTag });
            RebuildApiKeyViews();
        }

        UpdateSessionPaceSummary();
        RefreshReadinessChecklist();
        SaveAllSettingsSilently();
    }

    private string GetSelectedLaunchPlatform() =>
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
        var acct = _apiKeyVault.ActiveEntry?.DisplayLabel;
        SessionPaceSummaryText.Text = string.IsNullOrWhiteSpace(acct)
            ? $"{paceText}  •  Platform: {platform}"
            : $"{paceText}  •  Platform: {platform} ({acct})";
    }

    private void AutoSellInventory_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingUi) return;
        SaveAllSettingsSilently(); // remembered across restarts; it used to reset to off on every launch
    }

    private void ArmLiveInput_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingUi) return;
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
            SessionSelectorsGrid is null || SessionKpiGrid is null || IncomeKpiGrid is null ||
            ScannerCoreFiltersGrid is null || ScannerAdvancedFiltersGrid is null)
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
        SessionSelectorsGrid.Columns = columns;
        SessionKpiGrid.Columns = columns;
        IncomeKpiGrid.Columns = columns;
        ScannerCoreFiltersGrid.Columns = columns;
        ScannerAdvancedFiltersGrid.Columns = columns;
        if (ReportDetailKpiGrid is not null)
        {
            ReportDetailKpiGrid.Columns = columns;
        }

        if (IncomeHeaderActionsPanel is not null)
        {
            if (compact)
            {
                Grid.SetRow(IncomeHeaderActionsPanel, 1);
                Grid.SetColumn(IncomeHeaderActionsPanel, 0);
                Grid.SetColumnSpan(IncomeHeaderActionsPanel, 2);
                IncomeHeaderActionsPanel.HorizontalAlignment = HorizontalAlignment.Left;
                IncomeHeaderActionsPanel.Margin = new Thickness(0, 10, 0, 0);
            }
            else
            {
                Grid.SetRow(IncomeHeaderActionsPanel, 0);
                Grid.SetColumn(IncomeHeaderActionsPanel, 1);
                Grid.SetColumnSpan(IncomeHeaderActionsPanel, 1);
                IncomeHeaderActionsPanel.HorizontalAlignment = HorizontalAlignment.Right;
                IncomeHeaderActionsPanel.Margin = new Thickness(0);
            }
        }

        if (IncomeChartsLeftCol is not null && IncomeChartsRightCol is not null &&
            DailyProfitChartCard is not null && HourlyActivityChartCard is not null)
        {
            if (narrowCharts)
            {
                IncomeChartsLeftCol.Width = new GridLength(1, GridUnitType.Star);
                IncomeChartsRightCol.Width = new GridLength(0);
                Grid.SetRow(DailyProfitChartCard, 0);
                Grid.SetColumn(DailyProfitChartCard, 0);
                DailyProfitChartCard.Margin = new Thickness(0, 0, 0, 12);
                Grid.SetRow(HourlyActivityChartCard, 1);
                Grid.SetColumn(HourlyActivityChartCard, 0);
            }
            else
            {
                IncomeChartsLeftCol.Width = new GridLength(1.2, GridUnitType.Star);
                IncomeChartsRightCol.Width = new GridLength(1, GridUnitType.Star);
                Grid.SetRow(DailyProfitChartCard, 0);
                Grid.SetColumn(DailyProfitChartCard, 0);
                DailyProfitChartCard.Margin = new Thickness(0, 0, 12, 0);
                Grid.SetRow(HourlyActivityChartCard, 0);
                Grid.SetColumn(HourlyActivityChartCard, 1);
            }
        }

        if (ListsLeftCol is not null && ListsRightCol is not null &&
            ListsTopRow is not null && ListsBottomRow is not null &&
            CustomListCard is not null && BlacklistCard is not null)
        {
            if (mediumCompact)
            {
                ListsLeftCol.Width = new GridLength(1, GridUnitType.Star);
                ListsRightCol.Width = new GridLength(0);
                ListsTopRow.Height = new GridLength(1, GridUnitType.Star);
                ListsBottomRow.Height = new GridLength(1, GridUnitType.Star);
                Grid.SetRow(CustomListCard, 0);
                Grid.SetColumn(CustomListCard, 0);
                CustomListCard.Margin = new Thickness(0, 0, 0, 12);
                Grid.SetRow(BlacklistCard, 1);
                Grid.SetColumn(BlacklistCard, 0);
            }
            else
            {
                ListsLeftCol.Width = new GridLength(1.2, GridUnitType.Star);
                ListsRightCol.Width = new GridLength(1, GridUnitType.Star);
                ListsTopRow.Height = new GridLength(1, GridUnitType.Star);
                ListsBottomRow.Height = GridLength.Auto;
                Grid.SetRow(CustomListCard, 0);
                Grid.SetColumn(CustomListCard, 0);
                CustomListCard.Margin = new Thickness(0, 0, 14, 0);
                Grid.SetRow(BlacklistCard, 0);
                Grid.SetColumn(BlacklistCard, 1);
            }
        }

        if (ReportsLeftCol is not null && ReportsRightCol is not null &&
            ReportsTopRow is not null && ReportsBottomRow is not null &&
            ReportsListCard is not null && ReportDetailCard is not null)
        {
            if (compact)
            {
                ReportsLeftCol.Width = new GridLength(1, GridUnitType.Star);
                ReportsRightCol.Width = new GridLength(0);
                ReportsTopRow.Height = new GridLength(200);
                ReportsBottomRow.Height = new GridLength(1, GridUnitType.Star);
                Grid.SetRow(ReportsListCard, 0);
                Grid.SetColumn(ReportsListCard, 0);
                ReportsListCard.Margin = new Thickness(0, 0, 0, 12);
                Grid.SetRow(ReportDetailCard, 1);
                Grid.SetColumn(ReportDetailCard, 0);
            }
            else
            {
                ReportsLeftCol.Width = new GridLength(300);
                ReportsRightCol.Width = new GridLength(1, GridUnitType.Star);
                ReportsTopRow.Height = new GridLength(1, GridUnitType.Star);
                ReportsBottomRow.Height = GridLength.Auto;
                Grid.SetRow(ReportsListCard, 0);
                Grid.SetColumn(ReportsListCard, 0);
                ReportsListCard.Margin = new Thickness(0, 0, 14, 0);
                Grid.SetRow(ReportDetailCard, 0);
                Grid.SetColumn(ReportDetailCard, 1);
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
        ApplyResponsiveLayout(Width);
    }

    private async Task FlashButtonFeedbackAsync(Button? button, string temporaryText, int durationMs = 1600)
    {
        if (button is null) return;
        var dispatcher = button.Dispatcher;
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;

        object? originalContent = null;
        try
        {
            await dispatcher.InvokeAsync(() =>
            {
                if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;
                originalContent = button.Content;
                button.Content = temporaryText;
            });

            await Task.Delay(durationMs).ConfigureAwait(false);

            if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;
            _ = dispatcher.InvokeAsync(() =>
            {
                if (!dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished)
                {
                    button.Content = originalContent;
                }
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (InvalidOperationException)
        {
        }
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
        if (!TryReadLimits(out var limits)) return;
        if (!TryReadClientSettings(out var clientSettings)) return;
        if (!TryReadOperatorSettings(out var opSettings)) return;

        _saveLimits(limits);
        _saveClientSettings(clientSettings);
        _saveOperatorSettings(opSettings);

        IReadOnlyList<ItemCandidate>? inlineCandidates = opSettings.UseCustomShoppingList && _customShoppingList.Count > 0
            ? _customShoppingList.ToArray()
            : null;
        IReadOnlyList<string>? inlineBlacklist = _blacklist.Count > 0
            ? _blacklist.ToArray()
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
        if (ReadAutoRescanHours() > 0 &&
            string.Equals(Path.GetFullPath(NullIfBlank(CandidatesPathBox.Text) ?? "."), Path.GetFullPath(ScannedCandidatesPath), StringComparison.OrdinalIgnoreCase) &&
            TryReadScanFilter(out var currentScanFilter, out _))
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
            AutoRescanIntervalMinutes: autoRescanFilter is null ? 0 : (int)Math.Round(ReadAutoRescanHours() * 60m)), PayloadJson);
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
    // Income & Portfolio Tracker
    // ---------------------------------------------------------------------

    private Task LoadCachedPortfolioOnStartupAsync() => ReloadLedgerForActiveAccountAsync();

    private async Task ReloadLedgerForActiveAccountAsync()
    {
        _archivedTransactions = Array.Empty<Gw2LedgerTransaction>();
        _latestEquitySnapshot = null;
        _latestDelivery = null;
        _latestActiveOrders.Clear();
        _activeOrderRows.Clear();
        _openBuyCount = 0;
        _openSellCount = 0;
        _outbidBuyCount = 0;
        _undercutSellCount = 0;

        if (_ledgerStore is null)
        {
            RecomputeIncomeDashboard();
            return;
        }

        try
        {
            await _ledgerStore.InitializeAsync();
            var accountFilter = ResolveActiveSqlAccountName();
            _archivedTransactions = await _ledgerStore.GetTransactionsAsync(accountFilter);
            var snapshots = await _ledgerStore.GetEquitySnapshotsAsync(null, accountFilter);
            _latestEquitySnapshot = snapshots.LastOrDefault();
            if (_archivedTransactions.Count > 0 || _latestEquitySnapshot is not null)
            {
                var acctLabel = _apiKeyVault.ActiveEntry?.DisplayLabel ?? "local archive";
                IncomeSyncStatusText.Text =
                    $"Loaded {_archivedTransactions.Count:N0} archived transactions for {acctLabel}." +
                    (_latestEquitySnapshot is not null
                        ? $" Last equity snapshot: {_latestEquitySnapshot.CapturedAt.ToLocalTime():g}."
                        : string.Empty);
            }
            else
            {
                IncomeSyncStatusText.Text = "No archived transactions for this account yet. Click 'Sync GW2 Account' after configuring your API key.";
            }

            RecomputeIncomeDashboard();
        }
        catch (Exception ex)
        {
            AddLog($"Note loading cached income ledger: {ex.Message}");
        }
    }

    private async void SyncPortfolio_Click(object sender, RoutedEventArgs e)
    {
        var key = ResolveConfiguredApiKey();
        if (string.IsNullOrWhiteSpace(key))
        {
            IncomeSyncStatusText.Text = "No GW2 API key configured. Open Settings, paste your key, and click Save & Verify Key.";
            return;
        }

        if (_ledgerStore is null)
        {
            IncomeSyncStatusText.Text = "Portfolio SQLite store is unavailable in this instance.";
            return;
        }

        var originalSyncContent = SyncPortfolioButton.Content;
        SyncPortfolioButton.IsEnabled = false;
        SyncPortfolioButton.Content = "Syncing...";
        IncomeSyncStatusText.Text = "Syncing history, wallet, delivery box, and active orders from api.guildwars2.com...";
        var syncSucceeded = false;
        try
        {
            using var cts = CreateApiCancellationTokenSource();
            var provider = new Gw2OfficialDataProvider(_gw2HttpClient, key);
            var result = await provider.SyncPortfolioAsync(_ledgerStore, ResolveActiveSqlAccountName(), cts.Token);

            var resolvedAccount = !string.IsNullOrWhiteSpace(result.AccountName)
                ? result.AccountName
                : ResolveActiveSqlAccountName();
            if (!string.IsNullOrWhiteSpace(resolvedAccount) &&
                _apiKeyVault.ActiveEntry is { } activeEntry &&
                !string.Equals(activeEntry.AccountName, resolvedAccount, StringComparison.OrdinalIgnoreCase))
            {
                UpdateVaultEntry(activeEntry.Id, e => e with
                {
                    AccountName = resolvedAccount,
                    Status = "VALID",
                    LastWalletCopper = result.LatestSnapshot.WalletCopper,
                    LastVerifiedAt = DateTimeOffset.UtcNow
                });
                _hasVerifiedApiKey = true;
                RebuildApiKeyViews();
            }

            _archivedTransactions = await _ledgerStore.GetTransactionsAsync(resolvedAccount);
            _latestEquitySnapshot = result.LatestSnapshot;
            _latestDelivery = result.Delivery;

            _latestActiveOrders.Clear();
            _activeOrderRows.Clear();
            _openBuyCount = 0;
            _openSellCount = 0;
            _outbidBuyCount = 0;
            _undercutSellCount = 0;

            foreach (var order in result.ActiveOrders)
            {
                _latestActiveOrders.Add(order);
                _activeOrderRows.Add(new ActiveOrderViewRow(order));
                if (order.Kind == TransactionType.Buy)
                {
                    _openBuyCount++;
                    if (order.BookStatus == "Outbid") _outbidBuyCount++;
                }
                else
                {
                    _openSellCount++;
                    if (order.BookStatus == "Undercut") _undercutSellCount++;
                }
            }

            _hasVerifiedApiKey = true;
            syncSucceeded = true;
            RecomputeIncomeDashboard();
            RefreshReadinessChecklist();

            IncomeSyncStatusText.Text =
                $"Synced {resolvedAccount ?? "account"} at {DateTime.Now:T} — +{result.NewlyArchivedTransactions:N0} new fills ({result.TotalArchivedTransactions:N0} total archived).";
            AddLog($"Portfolio Sync ({resolvedAccount ?? "Account"}): +{result.NewlyArchivedTransactions} new fills; equity {MarketScanItem.FormatCoins(result.LatestSnapshot.TotalEquityCopper)}.");
        }
        catch (Exception ex)
        {
            IncomeSyncStatusText.Text = $"Sync failed: {ex.Message}";
            AddLog($"ERROR (Portfolio Sync): {ex.Message}");
        }
        finally
        {
            SyncPortfolioButton.IsEnabled = true;
            SyncPortfolioButton.Content = originalSyncContent;
            if (syncSucceeded)
            {
                await FlashButtonFeedbackAsync(SyncPortfolioButton, "✓ Synced");
            }
        }
    }

    private void IncomeTimeframe_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingUi) return;
        RecomputeIncomeDashboard();
    }

    private void IncomeSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loadingUi) return;
        ApplyItemPnlSearchFilter();
    }

    private void BlacklistSelectedPnlItem_Click(object sender, RoutedEventArgs e)
    {
        if (ItemPnlGrid.SelectedItem is not ItemPnlRow selected)
        {
            IncomeSyncStatusText.Text = "Select an item row in the Item P&L table first.";
            return;
        }

        AddBlacklistEntry(selected.ItemName);
        IncomeSyncStatusText.Text = $"Added '{selected.ItemName}' to Blacklist.";
        AddLog($"Blacklisted '{selected.ItemName}' from Income Leaderboard.");
    }

    private void ExportLedgerCsv_Click(object sender, RoutedEventArgs e)
    {
        if (_archivedTransactions.Count == 0)
        {
            IncomeSyncStatusText.Text = "No archived transactions to export yet. Click Sync GW2 Account first.";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Export Synced GW2 Transaction Ledger CSV",
            FileName = $"gw2_ledger_{DateTime.Now:yyyyMMdd}.csv",
            Filter = "CSV (*.csv)|*.csv"
        };
        if (dialog.ShowDialog(this) != true) return;

        TradingIncomeAnalytics.ExportLedgerCsv(dialog.FileName, _archivedTransactions);
        IncomeSyncStatusText.Text = $"Exported {_archivedTransactions.Count:N0} transactions to {Path.GetFileName(dialog.FileName)}.";
        AddLog($"Exported synced ledger ({_archivedTransactions.Count} rows) to {dialog.FileName}.");
    }

    private void RecomputeIncomeDashboard()
    {
        var windowStart = ResolveSelectedTimeframeStartUtc();
        var report = TradingIncomeAnalytics.Analyze(_archivedTransactions, windowStart);
        _lastIncomeReport = report;
        _allTimeframePnlRows = report.ItemLeaderboard;

        IncomeMatchedProfitText.Text = MarketScanItem.FormatCoins(report.MatchedProfitCopper);
        var avgDayText = MarketScanItem.FormatCoins((long)Math.Round(report.AvgProfitPerActiveDayCopper));
        IncomeMatchedSubText.Text =
            $"ROI: {report.MatchedRoiPercent:0.0}%  •  Win: {report.WinRatePercent:0}%  •  Avg {avgDayText}/day";

        if (_latestEquitySnapshot is { } snap)
        {
            IncomeTotalEquityText.Text = MarketScanItem.FormatCoins(snap.TotalEquityCopper);
            IncomeEquityBreakdownText.Text =
                $"Wallet {MarketScanItem.FormatCoins(snap.WalletCopper)} • " +
                $"Buys {MarketScanItem.FormatCoins(snap.ActiveBuysCopper)} • " +
                $"Sells(90%) {MarketScanItem.FormatCoins(snap.ActiveSellsNetCopper)}";
            var deliveryTotal = snap.DeliveryCopper + snap.DeliveryItemsCopper;
            IncomeDeliveryText.Text = MarketScanItem.FormatCoins(deliveryTotal);
            var itemCount = _latestDelivery?.Items.Sum(i => i.Count) ?? 0;
            IncomeUnmatchedText.Text =
                $"Delivery: {MarketScanItem.FormatCoins(snap.DeliveryCopper)} ({itemCount} items) • Unmatched loot: {MarketScanItem.FormatCoins(report.UnmatchedSellRevenueCopper)}";
        }
        else
        {
            IncomeTotalEquityText.Text = "—";
            IncomeEquityBreakdownText.Text = "Sync account to snapshot Wallet + Buys + 90% Sells";
            IncomeDeliveryText.Text = "—";
            IncomeUnmatchedText.Text = $"Unmatched loot/pre-sync sales: {MarketScanItem.FormatCoins(report.UnmatchedSellRevenueCopper)}";
        }

        var openTotal = _openBuyCount + _openSellCount;
        IncomeOrdersHealthText.Text = $"{openTotal} open ({_openBuyCount}B / {_openSellCount}S)";
        IncomeOrdersSubText.Text =
            $"Outbid buys: {_outbidBuyCount} (free to update)  •  Undercut sells: {_undercutSellCount} (hold)";
        PortfolioInsightsText.Text = PortfolioProfitAdvisor.BuildDashboardInsightSummary(report, _latestActiveOrders, DateTimeOffset.UtcNow);

        ApplyItemPnlSearchFilter();

        _ledgerRows.Clear();
        var filteredLedger = _archivedTransactions
            .Where(t => !windowStart.HasValue || t.PurchasedAt >= windowStart.Value)
            .OrderByDescending(t => t.PurchasedAt)
            .ThenByDescending(t => t.Id)
            .Take(500);
        foreach (var tx in filteredLedger)
        {
            _ledgerRows.Add(new LedgerHistoryViewRow(tx));
        }

        RebuildIncomeCharts(report);
    }

    private void ApplyItemPnlSearchFilter()
    {
        var query = IncomeSearchBox.Text?.Trim();
        _itemPnlRows.Clear();
        foreach (var row in _allTimeframePnlRows)
        {
            if (!string.IsNullOrWhiteSpace(query) &&
                !row.ItemName.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            _itemPnlRows.Add(row);
        }
    }

    private void RebuildIncomeCharts(TradingIncomeReport report)
    {
        var successBrush = ResolveBrush("SuccessBrush", Brushes.SeaGreen);
        var dangerBrush = ResolveBrush("DangerBrush", Brushes.IndianRed);
        var accentBrush = ResolveBrush("AccentBrush", Brushes.DodgerBlue);
        var goldBrush = ResolveBrush("GoldAccentBrush", Brushes.Goldenrod);
        var mutedBrush = ResolveBrush("BorderBrush", Brushes.Gray);

        _dailyChartBars.Clear();
        var recentDays = report.DailySeries.TakeLast(24).ToArray();
        var maxDailyAbs = recentDays.Length > 0
            ? Math.Max(1L, recentDays.Max(d => Math.Abs(d.ProfitCopper)))
            : 1L;
        foreach (var day in recentDays)
        {
            var ratio = Math.Abs(day.ProfitCopper) / (double)maxDailyAbs;
            var height = day.ProfitCopper == 0 ? 4.0 : Math.Max(8.0, ratio * 84.0);
            var brush = day.ProfitCopper > 0 ? successBrush : day.ProfitCopper < 0 ? dangerBrush : mutedBrush;
            _dailyChartBars.Add(new ChartBarItem(day.Label, height, brush, day.Tooltip, MarketScanItem.FormatCoins(day.ProfitCopper)));
        }

        DailyChartSummaryText.Text = recentDays.Length > 0
            ? $"{recentDays.Length} day(s) • Total {MarketScanItem.FormatCoins(report.MatchedProfitCopper)}"
            : "No closed flips in timeframe";

        _hourlyChartBars.Clear();
        var hasProfitBars = report.HourlySeries.Any(h => h.RealizedProfitCopper != 0);
        var bestHour = report.HourlySeries.OrderByDescending(h => h.RealizedProfitCopper).FirstOrDefault();

        foreach (var hour in report.HourlySeries)
        {
            double ratio;
            Brush brush;
            if (hasProfitBars)
            {
                var maxAbsProf = Math.Max(1L, report.HourlySeries.Max(h => Math.Abs(h.RealizedProfitCopper)));
                ratio = Math.Abs(hour.RealizedProfitCopper) / (double)maxAbsProf;
                brush = hour.RealizedProfitCopper > 0 ? goldBrush : hour.RealizedProfitCopper < 0 ? dangerBrush : mutedBrush;
            }
            else
            {
                var count = hour.BuyFills + hour.SellFills;
                var maxCount = Math.Max(1, report.HourlySeries.Max(h => h.BuyFills + h.SellFills));
                ratio = count / (double)maxCount;
                brush = count > 0 ? accentBrush : mutedBrush;
            }

            var hasActivity = hour.BuyFills + hour.SellFills > 0 || hour.RealizedProfitCopper != 0;
            var height = hasActivity ? Math.Max(8.0, ratio * 84.0) : 4.0;
            var label = hour.LocalHour % 3 == 0 ? hour.HourLabel : string.Empty;
            _hourlyChartBars.Add(new ChartBarItem(label, height, brush, hour.Tooltip, string.Empty));
        }

        HourlyChartSummaryText.Text = bestHour is { RealizedProfitCopper: > 0 }
            ? $"Best local hour: {bestHour.LocalHour:00}:00 ({MarketScanItem.FormatCoins(bestHour.RealizedProfitCopper)})"
            : "Local timezone 00–23h";
    }

    private DateTimeOffset? ResolveSelectedTimeframeStartUtc()
    {
        var daysTag = (IncomeTimeframeSelector.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        if (!int.TryParse(daysTag, out var days) || days <= 0)
        {
            return null;
        }

        if (days == 1)
        {
            var localStart = new DateTimeOffset(DateTime.Now.Date, TimeZoneInfo.Local.GetUtcOffset(DateTime.Now));
            return localStart.ToUniversalTime();
        }

        return DateTimeOffset.UtcNow.AddDays(-days);
    }

    private Brush ResolveBrush(string key, Brush fallback) =>
        Application.Current?.TryFindResource(key) is Brush b ? b : fallback;

    // ---------------------------------------------------------------------
    // Market Scanner (Built-in Profitable Item Finder)
    // ---------------------------------------------------------------------

    private void ApplyScannerPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.CommandParameter is not string presetName) return;
        var preset = TradingStrategyPresets.FindByName(presetName);
        if (preset is null) return;

        ApplyStrategyPresetToScanner(preset);
        ScanStatusText.Text = $"Applied '{preset.Name}' preset: {preset.Rationale}";
        AddLog($"Scanner Preset '{preset.Name}': {preset.Description}");
    }

    private async void AutoTuneForWallet_Click(object sender, RoutedEventArgs e)
    {
        var clickedButton = sender as Button;
        long capitalCopper = _latestEquitySnapshot?.WalletCopper ?? 0;
        var sourceLabel = "cached snapshot";

        var key = ResolveConfiguredApiKey();
        if (!string.IsNullOrWhiteSpace(key))
        {
            try
            {
                using var cts = CreateApiCancellationTokenSource();
                var provider = new Gw2OfficialDataProvider(_gw2HttpClient, key);
                var balance = await provider.GetBalanceAsync(cts.Token);
                if (balance is not null)
                {
                    capitalCopper = balance.Copper;
                    sourceLabel = "live GW2 wallet";
                    _hasVerifiedApiKey = true;
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

        var preset = TradingStrategyPresets.RecommendForCapital(capitalCopper);
        ApplyStrategyPresetToScanner(preset);
        ApplyStrategyPresetToSettings(preset);
        SaveAllSettingsSilently();
        RefreshReadinessChecklist();

        var message = $"Auto-tuned from {sourceLabel}: {preset.Rationale}";
        StatusText.Text = message;
        ScanStatusText.Text = message;
        AddLog($"Auto-Tune: {preset.Rationale}");
        await FlashButtonFeedbackAsync(clickedButton ?? AutoTuneSettingsButton, "✓ Tuned");
    }

    private void ApplyStrategyPresetToScanner(TradingStrategyPreset preset)
    {
        ScanMinRoiBox.Text = preset.MinRoiPercent.ToString(CultureInfo.InvariantCulture);
        ScanMaxRoiBox.Text = preset.MaxRoiPercent.ToString(CultureInfo.InvariantCulture);
        ScanMinProfitSilverBox.Text = preset.MinProfitSilver.ToString(CultureInfo.InvariantCulture);
        ScanMinBuySilverBox.Text = preset.MinBuySilver.ToString(CultureInfo.InvariantCulture);
        ScanMaxBuyGoldBox.Text = preset.MaxBuyGold.ToString(CultureInfo.InvariantCulture);
        ScanMinSupplyBox.Text = preset.MinSupply.ToString(CultureInfo.InvariantCulture);
        ScanMinDemandBox.Text = preset.MinDemand.ToString(CultureInfo.InvariantCulture);
        SelectComboByTag(ScanCategorySelector, preset.PreferredCategory);
    }

    private void ApplyStrategyPresetToSettings(TradingStrategyPreset preset)
    {
        MinimumProfitBox.Text = preset.MinRoiPercent.ToString(CultureInfo.CurrentCulture);
        MaximumBuyGoldBox.Text = preset.MaxBuyGold.ToString(CultureInfo.CurrentCulture);
        DefaultQuantityBox.Text = preset.DefaultOrderQuantity.ToString(CultureInfo.InvariantCulture);
        GoldThresholdNoBuyBox.Text = preset.RecommendedGoldFloor.ToString(CultureInfo.CurrentCulture);
        OverbidMinBox.Text = preset.OverbidMinCopper.ToString(CultureInfo.InvariantCulture);
        OverbidMaxBox.Text = preset.OverbidMaxCopper.ToString(CultureInfo.InvariantCulture);
        UndercutMinBox.Text = preset.UndercutMinCopper.ToString(CultureInfo.InvariantCulture);
        UndercutMaxBox.Text = preset.UndercutMaxCopper.ToString(CultureInfo.InvariantCulture);
        // Session cap and character-swap interval are the operator's own schedule, not a function of wallet size,
        // so Auto-Tune leaves them as configured.
    }

    private void ScanMarket_Click(object sender, RoutedEventArgs e) => RunMarketScanAsync(useCachedPrices: false);

    private void ReFilterCachedScan_Click(object sender, RoutedEventArgs e) => RunMarketScanAsync(useCachedPrices: true);

    private async void RunMarketScanAsync(bool useCachedPrices)
    {
        if (!TryReadScanFilter(out var filter, out var error))
        {
            ScanStatusText.Text = error ?? "Invalid scanner filter.";
            return;
        }

        _scanCancellation?.Cancel();
        _scanCancellation?.Dispose();
        _scanCancellation = new CancellationTokenSource();
        var ct = _scanCancellation.Token;

        ScanMarketButton.IsEnabled = false;
        ReFilterButton.IsEnabled = false;
        CancelScanButton.IsEnabled = true;
        ScanProgressBar.Value = 0;
        ScanStatusText.Text = useCachedPrices
            ? "Re-filtering cached Trading Post prices..."
            : "Starting GW2 API market scan...";

        var progress = new Progress<MarketScanProgress>(p =>
        {
            ScanProgressBar.Value = p.TotalItems > 0 ? Math.Min(100, p.ScannedItems * 100.0 / p.TotalItems) : 5;
            ScanStatusText.Text = p.TotalItems > 0
                ? $"{p.Phase} ({p.ScannedItems:N0}/{p.TotalItems:N0} checked • {p.MatchesFound:N0} matches)"
                : p.Phase;
        });

        try
        {
            var blacklistSnapshot = _blacklist.ToArray();
            IReadOnlyList<MarketScanItem> items;
            if (useCachedPrices && _marketScanner is Gw2MarketScanner concreteScanner && concreteScanner.HasCachedScan)
            {
                items = await concreteScanner.ReFilterCachedAsync(filter, blacklistSnapshot, progress, ct);
            }
            else
            {
                items = await _marketScanner.ScanAsync(filter, blacklistSnapshot, progress, ct);
            }

            _scanResults.Clear();
            foreach (var item in items)
            {
                _scanResults.Add(item);
            }

            ScanProgressBar.Value = 100;
            ScanStatusText.Text = $"Found {items.Count} profitable items matching your filters.";
            AddLog($"Market Scanner: Found {items.Count} profitable items.");
            SaveOperatorSettingsIfValid();
        }
        catch (OperationCanceledException)
        {
            ScanStatusText.Text = "Market scan cancelled.";
        }
        catch (Exception exception)
        {
            ScanStatusText.Text = $"Scan failed: {exception.Message}";
            AddLog($"ERROR (Market Scanner): {exception.Message}");
        }
        finally
        {
            ScanMarketButton.IsEnabled = true;
            ReFilterButton.IsEnabled = true;
            CancelScanButton.IsEnabled = false;
        }
    }

    private void CancelScan_Click(object sender, RoutedEventArgs e) => _scanCancellation?.Cancel();

    private async void ImportScannerFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import GW2BLTC / GW2Profits CSV or JSON",
            Filter = "Supported Lists (*.csv;*.json)|*.csv;*.json|All Files (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var imported = await Gw2MarketScanner.ImportFromFileAsync(dialog.FileName);
            _scanResults.Clear();
            foreach (var item in imported)
            {
                _scanResults.Add(item);
            }

            ScanStatusText.Text = $"Imported {imported.Count} items from {Path.GetFileName(dialog.FileName)}.";
            AddLog($"Imported {imported.Count} items into Market Scanner from {dialog.FileName}.");
        }
        catch (Exception exception)
        {
            ScanStatusText.Text = $"Import failed: {exception.Message}";
        }
    }

    private async void UseScanResultsForSession_Click(object sender, RoutedEventArgs e)
    {
        if (_scanResults.Count == 0)
        {
            ScanStatusText.Text = "No scanned items to send to session yet.";
            return;
        }

        var path = ScannedCandidatesPath;
        OperatorListsStore.SaveShoppingList(path, _scanResults.Select(item => item.ToCandidate()));
        CandidatesPathBox.Text = path;
        SaveOperatorSettingsIfValid();
        RefreshReadinessChecklist();
        ScanStatusText.Text = $"Saved {_scanResults.Count} items to session candidates ({Path.GetFileName(path)}).";
        AddLog($"Bound {_scanResults.Count} scanned items to session candidates.");
        await FlashButtonFeedbackAsync(UseScanResultsButton, "✓ Sent to Session");
    }

    private void AddSelectedScanToCustomList_Click(object sender, RoutedEventArgs e)
    {
        if (ScanResultsGrid.SelectedItem is not MarketScanItem selected)
        {
            ScanStatusText.Text = "Select an item in the table first.";
            return;
        }

        AddOrReplaceCustomCandidate(selected.ToCandidate());
        RefreshReadinessChecklist();
        ScanStatusText.Text = $"Added '{selected.ItemName}' to Custom Shopping List.";
    }

    private void BlacklistSelectedScanItem_Click(object sender, RoutedEventArgs e)
    {
        if (ScanResultsGrid.SelectedItem is not MarketScanItem selected)
        {
            ScanStatusText.Text = "Select an item in the table first.";
            return;
        }

        AddBlacklistEntry(selected.ItemName);
        _scanResults.Remove(selected);
        ScanStatusText.Text = $"Blacklisted '{selected.ItemName}'.";
    }

    // ---------------------------------------------------------------------
    // Custom Shopping List & Blacklist Editors
    // ---------------------------------------------------------------------

    private void CustomItemInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            AddCustomItem_Click(sender, e);
            e.Handled = true;
        }
    }

    private void BlacklistInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            AddBlacklistItem_Click(sender, e);
            e.Handled = true;
        }
    }

    private void AddCustomItem_Click(object sender, RoutedEventArgs e)
    {
        var name = NullIfBlank(CustomItemNameBox.Text);
        if (name is null) return;
        var rarity = (CustomItemRaritySelector.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Basic";
        var qty = long.TryParse(CustomItemQtyBox.Text.Trim(), out var parsedQty) && parsedQty > 0 ? parsedQty : 1;

        AddOrReplaceCustomCandidate(new ItemCandidate(name, Rarity: rarity, Quantity: qty));
        CustomItemNameBox.Clear();
        UpdateListCounts();
        RefreshReadinessChecklist();
    }

    private void RemoveCustomItem_Click(object sender, RoutedEventArgs e)
    {
        if (CustomShoppingListGrid.SelectedItem is not ItemCandidate selected) return;
        _customShoppingList.Remove(selected);
        OperatorListsStore.SaveShoppingList(CustomShoppingListPath, _customShoppingList);
        UpdateListCounts();
        RefreshReadinessChecklist();
    }

    private async void ImportCustomList_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import Custom Shopping List",
            Filter = "CSV or JSON (*.csv;*.json)|*.csv;*.json|All Files (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var loaded = await CandidatesLoader.LoadCandidatesAsync(dialog.FileName);
            foreach (var item in loaded)
            {
                AddOrReplaceCustomCandidate(item);
            }

            UpdateListCounts();
            RefreshReadinessChecklist();
            AddLog($"Imported {loaded.Count} items into Custom Shopping List.");
        }
        catch (Exception exception)
        {
            AddLog($"ERROR importing custom list: {exception.Message}");
        }
    }

    private void ExportCustomList_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export Custom Shopping List CSV",
            FileName = "custom_shoppinglist.csv",
            Filter = "CSV (*.csv)|*.csv"
        };
        if (dialog.ShowDialog(this) != true) return;

        OperatorListsStore.ExportShoppingListCsv(dialog.FileName, _customShoppingList);
        AddLog($"Exported {_customShoppingList.Count} custom items to {dialog.FileName}.");
    }

    private void UseCustomListForSession_Click(object sender, RoutedEventArgs e)
    {
        OperatorListsStore.SaveShoppingList(CustomShoppingListPath, _customShoppingList);
        UseCustomListOnStartCheckBox.IsChecked = true;
        SaveOperatorSettingsIfValid();
        RefreshReadinessChecklist();
        StatusText.Text = $"Custom Shopping List ({_customShoppingList.Count} items) ready for next session.";
        AddLog($"Custom Shopping List ({_customShoppingList.Count} items) enabled for Start session.");
    }

    private void AddBlacklistItem_Click(object sender, RoutedEventArgs e)
    {
        var entry = NullIfBlank(BlacklistInputBox.Text);
        if (entry is null) return;
        AddBlacklistEntry(entry);
        BlacklistInputBox.Clear();
        UpdateListCounts();
    }

    private void RemoveBlacklistItem_Click(object sender, RoutedEventArgs e)
    {
        if (BlacklistListBox.SelectedItem is not string selected) return;
        _blacklist.Remove(selected);
        OperatorListsStore.SaveBlacklist(BlacklistStorePath, _blacklist);
        UpdateListCounts();
    }

    private async void ImportBlacklist_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import Blacklist",
            Filter = "CSV or JSON (*.csv;*.json;*.txt)|*.csv;*.json;*.txt|All Files (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var entries = await CandidatesLoader.LoadBlacklistAsync(dialog.FileName);
            foreach (var entry in entries)
            {
                AddBlacklistEntry(entry);
            }

            UpdateListCounts();
            AddLog($"Imported {entries.Count} blacklist entries.");
        }
        catch (Exception exception)
        {
            AddLog($"ERROR importing blacklist: {exception.Message}");
        }
    }

    private void ExportBlacklist_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export Blacklist CSV",
            FileName = "blacklisted_items.csv",
            Filter = "CSV (*.csv)|*.csv"
        };
        if (dialog.ShowDialog(this) != true) return;

        OperatorListsStore.ExportBlacklistCsv(dialog.FileName, _blacklist);
        AddLog($"Exported {_blacklist.Count} blacklist entries to {dialog.FileName}.");
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
        if (_loadingUi || SidebarAccountSelector.SelectedItem is not ComboBoxItem { Tag: string keyId })
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
        var entry = _apiKeyVault.Entries.FirstOrDefault(item => string.Equals(item.Id, keyId, StringComparison.OrdinalIgnoreCase));
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

    private async void SaveCustomNames_Click(object sender, RoutedEventArgs e)
    {
        if (_apiKeyVault.Entries.Count == 0)
        {
            ApiKeyStatusText.Text = "Add an API key before saving custom names.";
            return;
        }

        var rowMap = _apiKeyRows.ToDictionary(r => r.Id, r => r.CustomName?.Trim() ?? string.Empty, StringComparer.OrdinalIgnoreCase);
        var updated = _apiKeyVault.Entries
            .Select(entry => rowMap.TryGetValue(entry.Id, out var custom)
                ? entry with { CustomName = custom }
                : entry)
            .ToArray();

        _apiKeyVault = _apiKeyVault with { Entries = updated };
        _saveApiKeyVault(_apiKeyVault);
        RebuildApiKeyViews();
        ApiKeyStatusText.Text = "Saved custom account labels to DPAPI vault.";
        AddLog("Saved custom account labels.");
        await FlashButtonFeedbackAsync(SaveCustomNamesButton, "✓ Saved");
    }

    private async void ReverifyApiKeys_Click(object sender, RoutedEventArgs e)
    {
        if (_apiKeyVault.Entries.Count == 0)
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
            var refreshed = new List<Gw2ApiKeyEntry>(_apiKeyVault.Entries.Count);
            foreach (var entry in _apiKeyVault.Entries)
            {
                try
                {
                    using var cts = CreateApiCancellationTokenSource();
                    var provider = new Gw2OfficialDataProvider(_gw2HttpClient, entry.ApiKey);
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

            _apiKeyVault = _apiKeyVault with { Entries = refreshed.ToArray() };
            _saveApiKeyVault(_apiKeyVault);
            _hasVerifiedApiKey = _apiKeyVault.ActiveEntry?.Status.Equals("VALID", StringComparison.OrdinalIgnoreCase) == true;
            RebuildApiKeyViews();
            RefreshReadinessChecklist();
            ApiKeyStatusText.Text = $"Re-verified {_apiKeyVault.Entries.Count} key(s): {verifiedCount} valid.";
            AddLog($"Re-verified API vault ({verifiedCount}/{_apiKeyVault.Entries.Count} valid).");
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
            var existing = _apiKeyVault.Entries.FirstOrDefault(item => string.Equals(item.ApiKey, raw, StringComparison.OrdinalIgnoreCase));
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
                var provider = new Gw2OfficialDataProvider(_gw2HttpClient, raw);
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
                    _hasVerifiedApiKey = true;
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

            var updatedEntries = _apiKeyVault.Entries
                .Select(item => string.Equals(item.Id, entryId, StringComparison.OrdinalIgnoreCase) ? newEntry : item)
                .ToList();
            if (existing is null)
            {
                updatedEntries.Add(newEntry);
            }

            _apiKeyVault = new Gw2ApiKeyVaultData(entryId, updatedEntries.ToArray());
            _saveApiKeyVault(_apiKeyVault);

            ApiKeyPasswordBox.Clear();
            ApiKeyCustomNameBox.Clear();
            RebuildApiKeyViews();
            RefreshReadinessChecklist();
            ApiKeyStatusText.Text = statusDetail;
            AddLog($"Saved DPAPI API key for {newEntry.DisplayLabel} ({status}).");
            saved = true;

            await ReloadLedgerForActiveAccountAsync();
            await LoadSessionReportsAsync();
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
        var activeEntry = _apiKeyVault.ActiveEntry;
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
            var provider = new Gw2OfficialDataProvider(_gw2HttpClient, key);
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
                _hasVerifiedApiKey = true;
                var coinText = result.CoinCopper.HasValue ? $" • Wallet: {MarketScanItem.FormatCoins(result.CoinCopper.Value)}" : string.Empty;
                var accountText = !string.IsNullOrWhiteSpace(result.AccountName) ? $"{result.AccountName}" : "Account";
                ApiKeyStatusText.Text = $"{accountText}{coinText} — {result.Message}";
                AddLog($"API key verified: {accountText}{coinText}");
            }
            else
            {
                _hasVerifiedApiKey = false;
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
        if (_apiKeyVault.ActiveEntry is { } active)
        {
            await RemoveVaultEntryAsync(active.Id);
            return;
        }

        _hasVerifiedApiKey = false;
        RebuildApiKeyViews();
        RefreshReadinessChecklist();
        AddLog("Cleared stored DPAPI GW2 API key.");
    }

    private void LatencyProfileSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingUi || LatencyProfileSelector.SelectedItem is not ComboBoxItem { Tag: string profileId })
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

    private async void DetectGw2_Click(object sender, RoutedEventArgs e)
    {
        var candidates = _discoverGw2Installs();
        var detected = candidates.FirstOrDefault()?.ExecutablePath ?? _detectGw2Path();
        var dpi = _detectDisplayScale();

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
                    _ = LoadSessionReportsAsync();
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

        var localTz = TimeZoneInfo.Local;
        var utcToday = new DateTimeOffset(nowUtc.UtcDateTime.Date, TimeSpan.Zero);
        var euStart = TimeZoneInfo.ConvertTime(utcToday.AddHours(17), localTz);
        var euEnd = TimeZoneInfo.ConvertTime(utcToday.AddHours(22), localTz);
        var resetStart = TimeZoneInfo.ConvertTime(utcToday.AddDays(1), localTz);
        var resetEnd = TimeZoneInfo.ConvertTime(utcToday.AddDays(1).AddMinutes(90), localTz);
        var naStart = TimeZoneInfo.ConvertTime(utcToday.AddHours(22), localTz);
        var naEnd = TimeZoneInfo.ConvertTime(utcToday.AddDays(1).AddHours(4), localTz);
        var offStart = TimeZoneInfo.ConvertTime(utcToday.AddDays(1).AddHours(4), localTz);
        var offEnd = TimeZoneInfo.ConvertTime(utcToday.AddDays(1).AddHours(17), localTz);

        EuPrimeTimeText.Text = $"{euStart:HH:mm}–{euEnd:HH:mm}";
        ResetTimeText.Text = $"{resetStart:HH:mm}–{resetEnd:HH:mm}";
        NaPrimeTimeText.Text = $"{naStart:HH:mm}–{naEnd:HH:mm}";
        OffPeakTimeText.Text = $"{offStart:HH:mm}–{offEnd:HH:mm}";

        SetScheduleTileHighlight(EuPrimeTile, info.PhaseName == "EU Prime Time");
        SetScheduleTileHighlight(ResetTile, info.PhaseName == "Daily Reset Spike");
        SetScheduleTileHighlight(NaPrimeTile, info.PhaseName == "NA Prime Time");
        SetScheduleTileHighlight(OffPeakTile, info.PhaseName == "Global Off-Peak");

        GuideScheduleSummaryText.Text = $"Now ({localTz.DisplayName}): {info.Summary} — {info.Advice}";
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

    private void SetScheduleTileHighlight(Border tile, bool isActive)
    {
        tile.Background = isActive
            ? ResolveBrush("AccentSoftBrush", ResolveBrush("SurfaceRaisedBrush", Brushes.DimGray))
            : ResolveBrush("SurfaceRaisedBrush", Brushes.DimGray);
        tile.BorderBrush = isActive
            ? ResolveBrush("AccentBrush", Brushes.DodgerBlue)
            : ResolveBrush("BorderBrush", Brushes.Gray);
        tile.BorderThickness = new Thickness(isActive ? 2 : 1);
    }

    private void RefreshReadinessChecklist()
    {
        var passBrush = ResolveBrush("SuccessBrush", Brushes.SeaGreen);
        var warnBrush = ResolveBrush("WarningBrush", Brushes.DarkOrange);
        var readyCount = 0;

        var hasKey = _hasVerifiedApiKey || !string.IsNullOrWhiteSpace(ResolveConfiguredApiKey());
        if (hasKey)
        {
            readyCount++;
            CheckApiKeyIcon.Text = "\uE73E";
            CheckApiKeyIcon.Foreground = passBrush;
            CheckApiKeyText.Text = _hasVerifiedApiKey
                ? "GW2 API Key verified and stored securely via Windows DPAPI."
                : "GW2 API Key configured (click Test Key in Settings to verify scopes).";
        }
        else
        {
            CheckApiKeyIcon.Text = "\uE7BA";
            CheckApiKeyIcon.Foreground = warnBrush;
            CheckApiKeyText.Text = "GW2 API Key missing — add a key with account, tradingpost, wallet scopes in Settings.";
        }

        var isSteamMode = string.Equals(GetSelectedLaunchPlatform(), Gw2ClientSettings.PlatformSteam, StringComparison.OrdinalIgnoreCase);
        var gw2Path = Gw2ExecutablePathBox.Text?.Trim();
        var hasGw2 = isSteamMode ||
                     (!string.IsNullOrWhiteSpace(gw2Path) &&
                      (Win32ProcessController.IsUriLaunch(gw2Path) || File.Exists(gw2Path)));
        if (hasGw2)
        {
            readyCount++;
            CheckGw2Icon.Text = "\uE73E";
            CheckGw2Icon.Foreground = passBrush;
            CheckGw2Text.Text = isSteamMode
                ? $"GW2 client configured: Steam Auto-Login ({Gw2ClientSettings.SteamLaunchUri})"
                : $"GW2 client configured: {gw2Path}";
        }
        else
        {
            CheckGw2Icon.Text = "\uE7BA";
            CheckGw2Icon.Foreground = warnBrush;
            CheckGw2Text.Text = string.IsNullOrWhiteSpace(gw2Path)
                ? "GW2 client path not set — click Auto-Detect GW2 or Browse in Settings."
                : $"Configured GW2 path not found on disk: {gw2Path}";
        }

        var dpi = _detectDisplayScale();
        if (dpi.IsRecommendedScale)
        {
            readyCount++;
            CheckDpiIcon.Text = "\uE73E";
            CheckDpiIcon.Foreground = passBrush;
        }
        else
        {
            CheckDpiIcon.Text = "\uE7BA";
            CheckDpiIcon.Foreground = warnBrush;
        }

        CheckDpiText.Text = $"Windows Display Scaling: {dpi.Summary}";

        var hasCandidates = _customShoppingList.Count > 0 || !string.IsNullOrWhiteSpace(CandidatesPathBox.Text);
        if (hasCandidates)
        {
            readyCount++;
            CheckCandidatesIcon.Text = "\uE73E";
            CheckCandidatesIcon.Foreground = passBrush;
            CheckCandidatesText.Text = _customShoppingList.Count > 0
                ? $"Flip targets ready ({_customShoppingList.Count} custom items + optional session file)."
                : $"Flip targets ready ({Path.GetFileName(CandidatesPathBox.Text.Trim())}).";
        }
        else
        {
            CheckCandidatesIcon.Text = "\uE7BA";
            CheckCandidatesIcon.Foreground = warnBrush;
            CheckCandidatesText.Text = "No flip targets loaded yet — run Market Scanner and click 'Use All for Session'.";
        }

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

    private CancellationTokenSource CreateApiCancellationTokenSource() =>
        new(TimeSpan.FromSeconds(GetConfiguredApiTimeoutSeconds()));

    private string? ResolveConfiguredApiKey() =>
        _apiKeyVault.ActiveEntry?.ApiKey?.Trim() is { Length: > 0 } vaultKey
            ? vaultKey
            : Environment.GetEnvironmentVariable("MMOPAL_Native__Gw2ApiKey");

    private string? ResolveActiveSqlAccountName()
    {
        var name = _apiKeyVault.ActiveEntry?.AccountName?.Trim();
        if (string.IsNullOrWhiteSpace(name) || string.Equals(name, "Default Account", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return name;
    }

    private void LoadApiKeyVault()
    {
        _apiKeyVault = _loadApiKeyVault();
        _hasVerifiedApiKey = _apiKeyVault.ActiveEntry?.Status.Equals("VALID", StringComparison.OrdinalIgnoreCase) == true;
        if (!string.IsNullOrWhiteSpace(_apiKeyVault.ActiveEntry?.LaunchPlatform))
        {
            var wasLoading = _loadingUi;
            _loadingUi = true;
            SelectComboByTag(LaunchPlatformSelector, _apiKeyVault.ActiveEntry.LaunchPlatform);
            _loadingUi = wasLoading;
        }

        RebuildApiKeyViews();
    }

    private void RebuildApiKeyViews()
    {
        var passBrush = ResolveBrush("SuccessBrush", Brushes.SeaGreen);
        var warnBrush = ResolveBrush("WarningBrush", Brushes.DarkOrange);
        var dangerBrush = ResolveBrush("DangerBrush", Brushes.IndianRed);

        var activeId = _apiKeyVault.ActiveEntry?.Id;
        _apiKeyRows.Clear();
        for (var i = 0; i < _apiKeyVault.Entries.Count; i++)
        {
            var entry = _apiKeyVault.Entries[i];
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
                canMoveDown: i < _apiKeyVault.Entries.Count - 1,
                statusUpper,
                statusBrush,
                string.IsNullOrWhiteSpace(entry.AccountName) ? "Default Account" : entry.AccountName,
                string.IsNullOrWhiteSpace(entry.LaunchPlatform) ? "ArenaNet" : entry.LaunchPlatform,
                entry.MaskedKey,
                entry.CustomName));
        }

        var wasLoading = _loadingUi;
        _loadingUi = true;
        try
        {
            SidebarAccountSelector.Items.Clear();
            if (_apiKeyVault.Entries.Count == 0)
            {
                SidebarAccountSelector.Items.Add(new ComboBoxItem { Content = "No account added" });
                SidebarAccountSelector.SelectedIndex = 0;
                SidebarAccountSelector.IsEnabled = false;
            }
            else
            {
                SidebarAccountSelector.IsEnabled = true;
                var selectedIdx = 0;
                for (var i = 0; i < _apiKeyVault.Entries.Count; i++)
                {
                    var entry = _apiKeyVault.Entries[i];
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
            _loadingUi = wasLoading;
        }

        IncomeActiveAccountText.Text = _apiKeyVault.ActiveEntry is { } active
            ? $"Account: {active.DisplayLabel}"
            : "No active account";

        RefreshApiKeyIndicator();
        UpdateSessionPaceSummary();
        UpdateSidebarStatusTooltip();
    }

    private async Task SwitchActiveAccountAsync(string keyId)
    {
        if (string.Equals(_apiKeyVault.ActiveKeyId, keyId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!_apiKeyVault.Entries.Any(e => string.Equals(e.Id, keyId, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        _apiKeyVault = _apiKeyVault with { ActiveKeyId = keyId };
        _saveApiKeyVault(_apiKeyVault);
        _hasVerifiedApiKey = _apiKeyVault.ActiveEntry?.Status.Equals("VALID", StringComparison.OrdinalIgnoreCase) == true;
        if (!string.IsNullOrWhiteSpace(_apiKeyVault.ActiveEntry?.LaunchPlatform))
        {
            var wasLoading = _loadingUi;
            _loadingUi = true;
            SelectComboByTag(LaunchPlatformSelector, _apiKeyVault.ActiveEntry.LaunchPlatform);
            _loadingUi = wasLoading;
        }

        RebuildApiKeyViews();
        RefreshReadinessChecklist();
        SaveAllSettingsSilently();

        var activeLabel = _apiKeyVault.ActiveEntry?.DisplayLabel ?? "Account";
        AddLog($"Switched active account to {activeLabel}.");
        await ReloadLedgerForActiveAccountAsync();
        await LoadSessionReportsAsync();

        if (_supervisor is DesktopWorkerSupervisor concreteSupervisor && _lastWorkerState is WorkerState.Idle or WorkerState.Stopped)
        {
            await concreteSupervisor.RestartWorkerAsync();
        }
    }

    private void MoveApiKeyEntry(string keyId, int delta)
    {
        var list = _apiKeyVault.Entries.ToList();
        var index = list.FindIndex(item => string.Equals(item.Id, keyId, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return;
        var targetIndex = index + delta;
        if (targetIndex < 0 || targetIndex >= list.Count) return;

        (list[index], list[targetIndex]) = (list[targetIndex], list[index]);
        _apiKeyVault = _apiKeyVault with { Entries = list.ToArray() };
        _saveApiKeyVault(_apiKeyVault);
        RebuildApiKeyViews();
    }

    private void UpdateVaultEntry(string keyId, Func<Gw2ApiKeyEntry, Gw2ApiKeyEntry> transform)
    {
        var updated = _apiKeyVault.Entries
            .Select(item => string.Equals(item.Id, keyId, StringComparison.OrdinalIgnoreCase) ? transform(item) : item)
            .ToArray();
        _apiKeyVault = _apiKeyVault with { Entries = updated };
        _saveApiKeyVault(_apiKeyVault);
    }

    private async Task RemoveVaultEntryAsync(string keyId)
    {
        var remaining = _apiKeyVault.Entries
            .Where(item => !string.Equals(item.Id, keyId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var nextActiveId = string.Equals(_apiKeyVault.ActiveKeyId, keyId, StringComparison.OrdinalIgnoreCase)
            ? remaining.FirstOrDefault()?.Id
            : _apiKeyVault.ActiveKeyId;

        _apiKeyVault = new Gw2ApiKeyVaultData(nextActiveId, remaining);
        _saveApiKeyVault(_apiKeyVault);
        _hasVerifiedApiKey = _apiKeyVault.ActiveEntry?.Status.Equals("VALID", StringComparison.OrdinalIgnoreCase) == true;
        RebuildApiKeyViews();
        RefreshReadinessChecklist();
        AddLog("Removed API key from DPAPI vault.");

        await ReloadLedgerForActiveAccountAsync();
        await LoadSessionReportsAsync();
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
        var accountLabel = _apiKeyVault.ActiveEntry?.DisplayLabel ?? "None";
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

    private void AddLog(string message, string? rawMessage = null)
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

    private void AddOrReplaceCustomCandidate(ItemCandidate candidate)
    {
        var normalized = ItemNameNormalizer.Normalize(candidate.ItemName);
        for (var i = 0; i < _customShoppingList.Count; i++)
        {
            if (ItemNameNormalizer.Normalize(_customShoppingList[i].ItemName) == normalized)
            {
                _customShoppingList[i] = candidate;
                OperatorListsStore.SaveShoppingList(CustomShoppingListPath, _customShoppingList);
                UpdateListCounts();
                return;
            }
        }

        _customShoppingList.Add(candidate);
        OperatorListsStore.SaveShoppingList(CustomShoppingListPath, _customShoppingList);
        UpdateListCounts();
    }

    private void AddBlacklistEntry(string entry)
    {
        var trimmed = entry.Trim();
        if (string.IsNullOrWhiteSpace(trimmed)) return;
        if (_blacklist.Any(existing => existing.Equals(trimmed, StringComparison.OrdinalIgnoreCase))) return;
        _blacklist.Add(trimmed);
        OperatorListsStore.SaveBlacklist(BlacklistStorePath, _blacklist);
        UpdateListCounts();
    }

    private void UpdateListCounts()
    {
        if (CustomListCountText is not null)
        {
            CustomListCountText.Text = $"{_customShoppingList.Count} item{(_customShoppingList.Count == 1 ? string.Empty : "s")}";
        }

        if (BlacklistCountText is not null)
        {
            BlacklistCountText.Text = $"{_blacklist.Count} blocked";
        }
    }

    private void RefreshApiKeyIndicator()
    {
        if (_apiKeyVault.ActiveEntry is { } active)
        {
            var countNote = _apiKeyVault.Entries.Count > 1 ? $" ({_apiKeyVault.Entries.Count} accounts saved)" : string.Empty;
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

    private bool TryReadScanFilter(out MarketScanFilter filter, out string? error)
    {
        filter = new MarketScanFilter();
        error = null;
        if (!decimal.TryParse(ScanMinRoiBox.Text.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var minRoi) || minRoi < 0)
        {
            error = "Min ROI % must be 0 or greater.";
            return false;
        }

        decimal? maxRoi = null;
        if (!string.IsNullOrWhiteSpace(ScanMaxRoiBox.Text))
        {
            if (!decimal.TryParse(ScanMaxRoiBox.Text.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedMaxRoi) || parsedMaxRoi <= 0)
            {
                error = "Max ROI % must be greater than 0, or blank.";
                return false;
            }

            maxRoi = parsedMaxRoi;
        }

        if (!decimal.TryParse(ScanMinProfitSilverBox.Text.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var minProfitSilver) || minProfitSilver < 0)
        {
            error = "Min Profit (silver) must be 0 or greater.";
            return false;
        }

        if (!decimal.TryParse(ScanMinBuySilverBox.Text.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var minBuySilver) || minBuySilver < 0)
        {
            error = "Min Buy (silver) must be 0 or greater.";
            return false;
        }

        long? maxBuyCopper = null;
        if (!string.IsNullOrWhiteSpace(ScanMaxBuyGoldBox.Text))
        {
            if (!decimal.TryParse(ScanMaxBuyGoldBox.Text.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var maxBuyGold) || maxBuyGold <= 0)
            {
                error = "Max Buy (gold) must be positive or blank.";
                return false;
            }

            maxBuyCopper = (long)decimal.Round(maxBuyGold * 10_000m, 0, MidpointRounding.AwayFromZero);
        }

        if (!long.TryParse(ScanMinSupplyBox.Text.Trim(), out var minSupply) || minSupply < 0 ||
            !long.TryParse(ScanMinDemandBox.Text.Trim(), out var minDemand) || minDemand < 0)
        {
            error = "Min Supply and Min Demand must be whole numbers 0 or greater.";
            return false;
        }

        var minTradesText = ScanMinTradesPerDayBox.Text.Trim();
        double minTradesPerDay = 0;
        if (minTradesText.Length > 0 &&
            (!double.TryParse(minTradesText, NumberStyles.Number, CultureInfo.CurrentCulture, out minTradesPerDay) || minTradesPerDay < 0))
        {
            error = "Min trades/day must be a number 0 or greater (0 = off).";
            return false;
        }

        var rarity = (ScanRaritySelector.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        var itemType = (ScanCategorySelector.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        var sortMode = (ScanSortSelector.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? MarketScanSortModes.GoldPerDay;
        var scopeText = (ScanScopeSelector.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "2000";
        var maxScan = int.TryParse(scopeText, out var parsedScope) ? parsedScope : 2000;
        var defaultQty = long.TryParse(DefaultQuantityBox.Text.Trim(), out var parsedQty) && parsedQty > 0 ? parsedQty : 1;
        var overbid = int.TryParse(OverbidMinBox.Text.Trim(), out var parsedOb) && parsedOb >= 0 ? parsedOb : 0;
        var undercut = int.TryParse(UndercutMinBox.Text.Trim(), out var parsedUc) && parsedUc >= 0 ? parsedUc : 0;

        filter = new MarketScanFilter(
            MinimumProfitPercentage: minRoi,
            MinimumProfitCopper: (long)decimal.Round(minProfitSilver * 100m, 0, MidpointRounding.AwayFromZero),
            MinimumBuyPriceCopper: (long)decimal.Round(minBuySilver * 100m, 0, MidpointRounding.AwayFromZero),
            MaximumBuyPriceCopper: maxBuyCopper,
            MinimumSupply: minSupply,
            MinimumDemand: minDemand,
            Rarity: rarity,
            MaxItemsToScan: maxScan,
            MaxResults: 150,
            DefaultQuantity: defaultQty,
            MaximumProfitPercentage: maxRoi,
            ItemType: itemType,
            OverbidCopper: overbid,
            UndercutCopper: undercut,
            CheckWallDepth: ScanCheckWallsCheckBox.IsChecked == true,
            IncludeDataWarsVelocity: ScanVelocityCheckBox.IsChecked == true,
            SortMode: sortMode,
            MinimumDailyTrades: minTradesPerDay);
        return true;
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string LimitsPath => Path.Combine(RuntimePaths.Create().Profiles, "trading-limits.json");
    private static string ClientSettingsPath => Path.Combine(RuntimePaths.Create().Profiles, "client-settings.json");
    private static string OperatorSettingsPath => Path.Combine(RuntimePaths.Create().Profiles, "operator-settings.json");
    private static string ApiKeyStorePath => Path.Combine(RuntimePaths.Create().Profiles, "gw2-api-key.dpapi");
    private static string ApiKeyVaultPath => Path.Combine(RuntimePaths.Create().Profiles, "gw2-api-keys.dpapi");
    private static string CustomShoppingListPath => Path.Combine(RuntimePaths.Create().Profiles, "custom-shoppinglist.json");
    private static string BlacklistStorePath => Path.Combine(RuntimePaths.Create().Profiles, "blacklist.json");
    private decimal ReadAutoRescanHours() =>
        decimal.TryParse(AutoRescanHoursBox?.Text?.Trim(), NumberStyles.Number, CultureInfo.CurrentCulture, out var hours) && hours > 0
            ? Math.Min(hours, 24m)
            : 0m;

    private static string ScannedCandidatesPath => Path.Combine(RuntimePaths.Create().Profiles, "scanned-candidates.json");
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
        ProtectSellListingsCheckBox.IsChecked = settings.ProtectSellListingsFromCancel;
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

        var dpi = _detectDisplayScale();
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
        ScanVelocityCheckBox.IsChecked = settings.EnableDataWarsVelocity;
        AutoSellInventoryCheckBox.IsChecked = settings.AutoListBoughtFlipItems;
        AutoRescanHoursBox.Text = settings.AutoRescanHours.ToString(CultureInfo.CurrentCulture);
        AutoDockBesideGw2CheckBox.IsChecked = settings.AutoDockSideBySideWhenRunning;
        UsePortfolioIntelligenceCheckBox.IsChecked = settings.UsePortfolioIntelligence;
        SaveSessionReportsCheckBox.IsChecked = settings.SaveSessionReports;
        ReportIncludeDryRunCheckBox.IsChecked = settings.ReportIncludeDryRunAndObserve;
        ReportSaveOnTradesCheckBox.IsChecked = settings.ReportAlwaysSaveIfTradesOccurred;
        ReportSaveOnErrorsCheckBox.IsChecked = settings.ReportAlwaysSaveOnErrors;
        ReportMinDurationBox.Text = Math.Clamp(settings.ReportMinDurationMinutes, 0, 1440).ToString(CultureInfo.InvariantCulture);
        MaxSavedReportsBox.Text = Math.Clamp(settings.MaxSavedSessionReports, 10, 1000).ToString(CultureInfo.InvariantCulture);
        UpdateReportRulesSummary(settings.ToSessionReportPolicy());
    }

    private void LoadOperatorLists(
        Func<IReadOnlyList<ItemCandidate>> loadCustomList,
        Func<IReadOnlyList<string>> loadBlacklist)
    {
        _customShoppingList.Clear();
        foreach (var item in loadCustomList()) _customShoppingList.Add(item);
        _blacklist.Clear();
        foreach (var entry in loadBlacklist()) _blacklist.Add(entry);
        UpdateListCounts();
    }

    private static void SelectComboByContent(ComboBox combo, string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return;
        for (var i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem item &&
                string.Equals(item.Content?.ToString(), content, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedIndex = i;
                return;
            }
        }
    }

    private static void SelectComboByTag(ComboBox combo, string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return;
        for (var i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem item &&
                string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedIndex = i;
                return;
            }
        }
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
            ProtectSellListingsFromCancel: ProtectSellListingsCheckBox.IsChecked == true,
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
            PriceEntryMode: (PriceEntryModeSelector.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? Gw2ClientSettings.PriceEntryClickRow);
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
            EnableDataWarsVelocity: ScanVelocityCheckBox.IsChecked == true,
            AutoListBoughtFlipItems: AutoSellInventoryCheckBox.IsChecked == true,
            AutoRescanHours: ReadAutoRescanHours(),
            AutoDockSideBySideWhenRunning: AutoDockBesideGw2CheckBox.IsChecked == true,
            UsePortfolioIntelligence: UsePortfolioIntelligenceCheckBox.IsChecked == true,
            SaveSessionReports: SaveSessionReportsCheckBox.IsChecked == true,
            ReportIncludeDryRunAndObserve: ReportIncludeDryRunCheckBox.IsChecked == true,
            ReportMinDurationMinutes: reportMinDuration,
            ReportAlwaysSaveIfTradesOccurred: ReportSaveOnTradesCheckBox.IsChecked == true,
            ReportAlwaysSaveOnErrors: ReportSaveOnErrorsCheckBox.IsChecked == true,
            MaxSavedSessionReports: maxSavedReports);
        UpdateReportRulesSummary(settings.ToSessionReportPolicy());
        return true;
    }

    private void SaveOperatorSettingsIfValid()
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

    private sealed record ChartBarItem(
        string Label,
        double BarHeight,
        Brush BarBrush,
        string Tooltip,
        string ValueLabel);

    private sealed class ActiveOrderViewRow(Gw2ActiveOrder order)
    {
        public string Side => order.Kind.ToString();
        public string ItemName => order.ItemName;
        public long Quantity => order.Quantity;
        public string YourPriceFormatted => MarketScanItem.FormatCoins(order.UnitPriceCopper);
        public string MarketTopFormatted => order.Kind == TransactionType.Buy
            ? (order.MarketHighestBuyCopper is { } b ? MarketScanItem.FormatCoins(b) : "—")
            : (order.MarketLowestSellCopper is { } s ? MarketScanItem.FormatCoins(s) : "—");
        public string BookStatus => order.BookStatus;
        public string CreatedFormatted => order.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    private sealed class LedgerHistoryViewRow(Gw2LedgerTransaction tx)
    {
        public string PurchasedFormatted => tx.PurchasedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        public string Side => tx.Kind.ToString();
        public string ItemName => tx.ItemName;
        public long Quantity => tx.Quantity;
        public string UnitPriceFormatted => MarketScanItem.FormatCoins(tx.UnitPriceCopper);
        public string GrossTotalFormatted => MarketScanItem.FormatCoins(tx.UnitPriceCopper * tx.Quantity);
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
    // Trade Session Reports & Max Cap Completion Countdown
    // ---------------------------------------------------------------------

    private async Task LoadSessionReportsAsync()
    {
        if (_sessionReportStore is null)
        {
            ApplyReportsFilter();
            return;
        }

        try
        {
            await _sessionReportStore.InitializeAsync();
            _allLoadedReports = await _sessionReportStore.GetReportsAsync(ResolveActiveSqlAccountName(), 100);
            ApplyReportsFilter();
        }
        catch (Exception ex)
        {
            AddLog($"Note loading saved session reports: {ex.Message}");
        }
    }

    private void ApplyReportsFilter()
    {
        if (_sessionReportRows is null || ReportsCountText is null || ReportOutcomeFilterSelector is null)
        {
            return;
        }

        var previouslySelectedId = _selectedReport?.Id;
        var filterTag = (ReportOutcomeFilterSelector.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "all";
        IEnumerable<TradingSessionReport> filtered = _allLoadedReports;
        if (filterTag == "completed")
        {
            filtered = filtered.Where(r => r.Outcome is "Completed" or "Time Cap Reached" or "Stopped by User");
        }
        else if (filterTag == "issues")
        {
            filtered = filtered.Where(r => r.HasIssues || r.Outcome is "Faulted" or "Needs Attention");
        }

        _sessionReportRows.Clear();
        foreach (var report in filtered)
        {
            var outcomeBrush = report.Outcome switch
            {
                "Time Cap Reached" => ResolveBrush("GoldAccentBrush", Brushes.Goldenrod),
                "Completed" => ResolveBrush("SuccessBrush", Brushes.SeaGreen),
                "Needs Attention" => ResolveBrush("WarningBrush", Brushes.DarkOrange),
                "Faulted" => ResolveBrush("DangerBrush", Brushes.IndianRed),
                _ => ResolveBrush("AccentBrush", Brushes.DodgerBlue)
            };
            _sessionReportRows.Add(new SessionReportListItem(report, outcomeBrush));
        }

        ReportsCountText.Text = $"{_sessionReportRows.Count} report{(_sessionReportRows.Count == 1 ? string.Empty : "s")}";
        if (_sessionReportRows.Count > 0)
        {
            var match = previouslySelectedId is not null
                ? _sessionReportRows.FirstOrDefault(r => string.Equals(r.Id, previouslySelectedId, StringComparison.OrdinalIgnoreCase))
                : null;
            SessionReportsListBox.SelectedItem = match ?? _sessionReportRows[0];
        }
        else
        {
            RenderSelectedReport(null);
        }
    }

    private void RenderSelectedReport(TradingSessionReport? report)
    {
        _selectedReport = report;
        if (report is null)
        {
            ReportEmptyStatePanel.Visibility = Visibility.Visible;
            ReportDetailContentPanel.Visibility = Visibility.Collapsed;
            _reportTradeRows.Clear();
            _reportIncidentRows.Clear();
            return;
        }

        ReportEmptyStatePanel.Visibility = Visibility.Collapsed;
        ReportDetailContentPanel.Visibility = Visibility.Visible;

        var outcomeBrush = report.Outcome switch
        {
            "Time Cap Reached" => ResolveBrush("GoldAccentBrush", Brushes.Goldenrod),
            "Completed" => ResolveBrush("SuccessBrush", Brushes.SeaGreen),
            "Needs Attention" => ResolveBrush("WarningBrush", Brushes.DarkOrange),
            "Faulted" => ResolveBrush("DangerBrush", Brushes.IndianRed),
            _ => ResolveBrush("AccentBrush", Brushes.DodgerBlue)
        };
        ReportDetailOutcomeText.Text = report.Outcome.ToUpperInvariant();
        ReportDetailOutcomeText.Foreground = outcomeBrush;
        ReportDetailTitleText.Text = $"{report.AccountName} — {report.RunMode} • Pace {report.Profile}";
        ReportDetailMetaText.Text =
            $"Started {report.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}  •  Ended {report.EndedAt.ToLocalTime():HH:mm:ss}  •  Source: {report.DataSource}";
        ReportDetailSummaryText.Text = report.OutcomeSummary;

        ReportKpiDurationText.Text = SessionReportFormatter.FormatDuration(report.DurationSeconds);
        ReportKpiCyclesText.Text = $"{report.CyclesExecuted:N0} cycles • {report.DecisionsEvaluated:N0} items";

        ReportKpiTradesText.Text = $"{report.TotalTradeActions:N0} action{(report.TotalTradeActions == 1 ? string.Empty : "s")}";
        ReportKpiTradeBreakdownText.Text = $"{report.BuyOrdersPlaced} buys • {report.SellOrdersListed} sells • {report.OutbidBuysRefreshed} refreshed";

        ReportKpiVolumeText.Text = MarketScanItem.FormatCoins(report.EstimatedBuyVolumeCopper);
        var startBal = report.StartingWalletCopper.HasValue ? MarketScanItem.FormatCoins(report.StartingWalletCopper.Value) : "—";
        var endBal = report.EndingWalletCopper.HasValue ? MarketScanItem.FormatCoins(report.EndingWalletCopper.Value) : "—";
        ReportKpiWalletText.Text = $"Wallet: {startBal} -> {endBal}";

        ReportKpiHealthText.Text = report.ErrorCount > 0
            ? $"{report.ErrorCount} error(s)"
            : report.WarningCount > 0 || report.RecoveryAttempts > 0 || report.GameRestarts > 0
                ? $"{report.WarningCount} warn • {report.RecoveryAttempts + report.GameRestarts} rec"
                : "Clean";
        ReportKpiHealthText.Foreground = report.ErrorCount > 0
            ? ResolveBrush("DangerBrush", Brushes.IndianRed)
            : report.HasIssues
                ? ResolveBrush("WarningBrush", Brushes.DarkOrange)
                : ResolveBrush("SuccessBrush", Brushes.SeaGreen);
        ReportKpiSwapsText.Text = $"{report.CharacterSwapsCompleted} swaps • {report.DeliveryCollections} delivery ({MarketScanItem.FormatCoins(report.DeliveryCoinsCollectedCopper)})";

        ReportTradesTabItem.Header = $"Trade & Action Log ({report.Trades.Count})";
        ReportIncidentsTabItem.Header = $"Incidents & Bugs ({report.Incidents.Count})";

        _reportTradeRows.Clear();
        foreach (var t in report.Trades)
        {
            _reportTradeRows.Add(new ReportTradeRow(t));
        }

        _reportIncidentRows.Clear();
        foreach (var inc in report.Incidents)
        {
            _reportIncidentRows.Add(new ReportIncidentRow(inc));
        }
    }

    private void UpdateReportRulesSummary(SessionReportPolicySettings policy)
    {
        if (ReportRulesSummaryText is null)
        {
            return;
        }

        if (!policy.SaveSessionReports)
        {
            ReportRulesSummaryText.Text = "Automatic session report archiving is currently disabled in Settings -> Hotkeys & Advanced.";
            return;
        }

        var modeScope = policy.IncludeDryRunAndObserve ? "Live, Dry run & Observe" : "Live";
        ReportRulesSummaryText.Text =
            $"Saving {modeScope} sessions when duration >= {policy.MinDurationMinutes}m" +
            (policy.AlwaysSaveIfTradesOccurred ? ", or any trade/swap occurred" : string.Empty) +
            (policy.AlwaysSaveOnErrors ? ", or any error/bug occurred" : string.Empty) +
            $". Keeps up to {policy.MaxSavedReports} reports.";
    }

    private void SessionReportsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingUi) return;
        var selected = SessionReportsListBox.SelectedItem as SessionReportListItem;
        RenderSelectedReport(selected?.Report);
    }

    private void ReportOutcomeFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingUi) return;
        ApplyReportsFilter();
    }

    private async void RefreshReports_Click(object sender, RoutedEventArgs e) => await LoadSessionReportsAsync();

    private void ExportReportMarkdown_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedReport is null)
        {
            StatusText.Text = "Select a session report first.";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Export Session Report (Markdown)",
            FileName = $"mmopal-session-{_selectedReport.StartedAt.ToLocalTime():yyyyMMdd-HHmm}.md",
            Filter = "Markdown (*.md)|*.md|Text (*.txt)|*.txt"
        };
        if (dialog.ShowDialog(this) != true) return;

        File.WriteAllText(dialog.FileName, SessionReportFormatter.ToMarkdown(_selectedReport));
        AddLog($"Exported Markdown report to {dialog.FileName}.");
    }

    private void ExportReportJson_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedReport is null)
        {
            StatusText.Text = "Select a session report first.";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Export Session Report (JSON)",
            FileName = $"mmopal-session-{_selectedReport.StartedAt.ToLocalTime():yyyyMMdd-HHmm}.json",
            Filter = "JSON (*.json)|*.json"
        };
        if (dialog.ShowDialog(this) != true) return;

        var pretty = JsonSerializer.Serialize(_selectedReport, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        File.WriteAllText(dialog.FileName, pretty);
        AddLog($"Exported JSON report to {dialog.FileName}.");
    }

    private async void DeleteReport_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedReport is null || _sessionReportStore is null)
        {
            return;
        }

        var id = _selectedReport.Id;
        await _sessionReportStore.DeleteReportAsync(id);
        await LoadSessionReportsAsync();
        AddLog($"Deleted session report {id}.");
    }

    private void CustomizeReportSettings_Click(object sender, RoutedEventArgs e)
    {
        NavSettings.IsChecked = true;
        if (SettingsTabControl is not null && SettingsTabControl.Items.Count >= 4)
        {
            SettingsTabControl.SelectedIndex = 3;
        }
    }

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

    private sealed class SessionReportListItem(TradingSessionReport report, Brush outcomeBrush)
    {
        public TradingSessionReport Report { get; } = report;
        public string Id => Report.Id;
        public string Outcome => Report.Outcome;
        public Brush OutcomeBrush { get; } = outcomeBrush;
        public string DurationFormatted => SessionReportFormatter.FormatDuration(Report.DurationSeconds);
        public string StartedFormatted => $"{Report.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm}  •  {Report.AccountName}";
        public string CompactStats =>
            $"{Report.RunMode} • {Report.TotalTradeActions} trades • {Report.CharacterSwapsCompleted} swaps • {Report.ErrorCount} errors";
    }

    private sealed class ReportTradeRow(SessionTradeEvent entry)
    {
        public string TimeFormatted => entry.Timestamp.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        public string ActionType => entry.ActionType;
        public string ItemName => entry.ItemName;
        public long Quantity => entry.Quantity;
        public string CoinsFormatted => entry.UnitPriceCopper > 0 ? MarketScanItem.FormatCoins(entry.UnitPriceCopper) : "—";
        public string Status => entry.Status;
        public string Details => entry.Details;
    }

    private sealed class ReportIncidentRow(SessionIncidentEvent entry)
    {
        public string TimeFormatted => entry.Timestamp.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        public string Severity => entry.Severity;
        public string Category => entry.Category;
        public string RecoveredLabel => entry.Recovered ? "Yes" : "No";
        public string Message => entry.Message;
    }
}
