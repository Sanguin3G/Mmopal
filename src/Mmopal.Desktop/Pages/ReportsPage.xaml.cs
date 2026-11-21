using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Mmopal.Contracts;
using Mmopal.Gw2;
using static Mmopal.Desktop.UiHelpers;

namespace Mmopal.Desktop.Pages;

/// <summary>Session Reports: the saved session reports, their details and exports.</summary>
public partial class ReportsPage : UserControl
{
    private readonly ObservableCollection<SessionReportListItem> _sessionReportRows = new();
    private readonly ObservableCollection<ReportTradeRow> _reportTradeRows = new();
    private readonly ObservableCollection<ReportIncidentRow> _reportIncidentRows = new();
    private IReadOnlyList<TradingSessionReport> _allLoadedReports = Array.Empty<TradingSessionReport>();
    private TradingSessionReport? _selectedReport;
    private MainWindow? _host;

    public ReportsPage()
    {
        InitializeComponent();
        SessionReportsListBox.ItemsSource = _sessionReportRows;
        ReportTradesGrid.ItemsSource = _reportTradeRows;
        ReportIncidentsGrid.ItemsSource = _reportIncidentRows;
    }

    private MainWindow Host => _host ?? throw new InvalidOperationException("The page is not attached to the main window.");

    private bool Loading => _host is null || _host.IsLoadingUi;

    internal void Attach(MainWindow host) => _host = host;

    /// <summary>Outcome colours come from the theme, so the list is rebuilt after a theme change.</summary>
    internal void RefreshAfterThemeChange()
    {
        if (_allLoadedReports.Count > 0)
        {
            ApplyReportsFilter();
        }
    }

    internal async Task LoadSessionReportsAsync()
    {
        if (Host.SessionReportStore is null)
        {
            ApplyReportsFilter();
            return;
        }

        try
        {
            await Host.SessionReportStore.InitializeAsync();
            _allLoadedReports = await Host.SessionReportStore.GetReportsAsync(Host.ResolveActiveSqlAccountName(), 100);
            ApplyReportsFilter();
        }
        catch (Exception ex)
        {
            Host.AddLog($"Note loading saved session reports: {ex.Message}");
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

    internal void UpdateReportRulesSummary(SessionReportPolicySettings policy)
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
        if (Loading) return;
        var selected = SessionReportsListBox.SelectedItem as SessionReportListItem;
        RenderSelectedReport(selected?.Report);
    }

    private void ReportOutcomeFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Loading) return;
        ApplyReportsFilter();
    }

    private async void RefreshReports_Click(object sender, RoutedEventArgs e) => await LoadSessionReportsAsync();

    private void ExportReportMarkdown_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedReport is null)
        {
            Host.StatusText.Text = "Select a session report first.";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Export Session Report (Markdown)",
            FileName = $"mmopal-session-{_selectedReport.StartedAt.ToLocalTime():yyyyMMdd-HHmm}.md",
            Filter = "Markdown (*.md)|*.md|Text (*.txt)|*.txt"
        };
        if (dialog.ShowDialog(Host) != true) return;

        File.WriteAllText(dialog.FileName, SessionReportFormatter.ToMarkdown(_selectedReport));
        Host.AddLog($"Exported Markdown report to {dialog.FileName}.");
    }

    private void ExportReportJson_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedReport is null)
        {
            Host.StatusText.Text = "Select a session report first.";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Export Session Report (JSON)",
            FileName = $"mmopal-session-{_selectedReport.StartedAt.ToLocalTime():yyyyMMdd-HHmm}.json",
            Filter = "JSON (*.json)|*.json"
        };
        if (dialog.ShowDialog(Host) != true) return;

        var pretty = JsonSerializer.Serialize(_selectedReport, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        File.WriteAllText(dialog.FileName, pretty);
        Host.AddLog($"Exported JSON report to {dialog.FileName}.");
    }

    private async void DeleteReport_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedReport is null || Host.SessionReportStore is null)
        {
            return;
        }

        var id = _selectedReport.Id;
        await Host.SessionReportStore.DeleteReportAsync(id);
        await LoadSessionReportsAsync();
        Host.AddLog($"Deleted session report {id}.");
    }

    private void CustomizeReportSettings_Click(object sender, RoutedEventArgs e)
    {
        Host.NavSettings.IsChecked = true;
        if (Host.SettingsPage.SettingsTabControl is not null && Host.SettingsPage.SettingsTabControl.Items.Count >= 4)
        {
            Host.SettingsPage.SettingsTabControl.SelectedIndex = 3;
        }
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
