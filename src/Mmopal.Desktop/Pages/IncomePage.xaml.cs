using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Mmopal.Contracts;
using Mmopal.Gw2;
using static Mmopal.Desktop.UiHelpers;

namespace Mmopal.Desktop.Pages;

/// <summary>Income &amp; Portfolio: the synced trade ledger, net worth, open orders and profit charts.</summary>
public partial class IncomePage : UserControl
{
    private readonly ObservableCollection<ItemPnlRow> _itemPnlRows = new();
    private readonly ObservableCollection<ActiveOrderViewRow> _activeOrderRows = new();
    private readonly ObservableCollection<LedgerHistoryViewRow> _ledgerRows = new();
    private readonly ObservableCollection<ChartBarItem> _dailyChartBars = new();
    private readonly ObservableCollection<ChartBarItem> _hourlyChartBars = new();
    private readonly List<Gw2ActiveOrder> _latestActiveOrders = new();
    private IReadOnlyList<ItemPnlRow> _allTimeframePnlRows = Array.Empty<ItemPnlRow>();
    private TradingIncomeReport? _lastIncomeReport;
    private Gw2EquitySnapshot? _latestEquitySnapshot;
    private IReadOnlyList<Gw2EquitySnapshot> _equitySnapshots = Array.Empty<Gw2EquitySnapshot>();
    private Gw2DeliverySnapshot? _latestDelivery;
    private int _openBuyCount;
    private int _openSellCount;
    private int _outbidBuyCount;
    private int _undercutSellCount;
    private IReadOnlyList<string> _losingItems = Array.Empty<string>();
    private MainWindow? _host;

    public IncomePage()
    {
        InitializeComponent();
        ItemPnlGrid.ItemsSource = _itemPnlRows;
        ActiveOrdersGrid.ItemsSource = _activeOrderRows;
        LedgerHistoryGrid.ItemsSource = _ledgerRows;
        DailyProfitChartItems.ItemsSource = _dailyChartBars;
        HourlyActivityChartItems.ItemsSource = _hourlyChartBars;
    }

    private MainWindow Host => _host ?? throw new InvalidOperationException("The page is not attached to the main window.");

    private bool Loading => _host is null || _host.IsLoadingUi;

    internal void Attach(MainWindow host) => _host = host;

    internal IReadOnlyList<Gw2LedgerTransaction> ArchivedTransactions { get; private set; } = Array.Empty<Gw2LedgerTransaction>();

    internal Gw2EquitySnapshot? LatestEquitySnapshot => _latestEquitySnapshot;

    /// <summary>Chart colours come from the theme, so the charts are rebuilt after a theme change.</summary>
    internal void RefreshAfterThemeChange()
    {
        if (_lastIncomeReport is not null)
        {
            RebuildIncomeCharts(_lastIncomeReport);
        }
    }

    internal Task LoadCachedPortfolioOnStartupAsync() => ReloadLedgerForActiveAccountAsync();

    internal async Task ReloadLedgerForActiveAccountAsync()
    {
        ArchivedTransactions = Array.Empty<Gw2LedgerTransaction>();
        _latestEquitySnapshot = null;
        _latestDelivery = null;
        _latestActiveOrders.Clear();
        _activeOrderRows.Clear();
        _openBuyCount = 0;
        _openSellCount = 0;
        _outbidBuyCount = 0;
        _undercutSellCount = 0;

        if (Host.LedgerStore is null)
        {
            RecomputeIncomeDashboard();
            return;
        }

        try
        {
            await Host.LedgerStore.InitializeAsync();
            var accountFilter = Host.ResolveActiveSqlAccountName();
            ArchivedTransactions = await Host.LedgerStore.GetTransactionsAsync(accountFilter);
            var snapshots = await Host.LedgerStore.GetEquitySnapshotsAsync(null, accountFilter);
            _equitySnapshots = snapshots;
            _latestEquitySnapshot = snapshots.LastOrDefault();
            if (ArchivedTransactions.Count > 0 || _latestEquitySnapshot is not null)
            {
                var acctLabel = Host.ApiKeyVault.ActiveEntry?.DisplayLabel ?? "local archive";
                IncomeSyncStatusText.Text =
                    $"Loaded {ArchivedTransactions.Count:N0} archived transactions for {acctLabel}." +
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
            Host.AddLog($"Note loading cached income ledger: {ex.Message}");
        }
    }

    private async void SyncPortfolio_Click(object sender, RoutedEventArgs e) => await SyncPortfolioCoreAsync();

    internal async Task<bool> SyncPortfolioCoreAsync()
    {
        var key = Host.ResolveConfiguredApiKey();
        if (string.IsNullOrWhiteSpace(key))
        {
            IncomeSyncStatusText.Text = "No GW2 API key configured. Open Settings, paste your key, and click Save & Verify Key.";
            return false;
        }

        if (Host.LedgerStore is null)
        {
            IncomeSyncStatusText.Text = "Portfolio SQLite store is unavailable in this instance.";
            return false;
        }

        var originalSyncContent = SyncPortfolioButton.Content;
        SyncPortfolioButton.IsEnabled = false;
        SyncPortfolioButton.Content = "Syncing...";
        IncomeSyncStatusText.Text = "Syncing history, wallet, delivery box, and active orders from api.guildwars2.com...";
        var syncSucceeded = false;
        try
        {
            using var cts = Host.CreateApiCancellationTokenSource();
            var provider = new Gw2OfficialDataProvider(Host.Gw2HttpClient, key);
            var result = await provider.SyncPortfolioAsync(Host.LedgerStore, Host.ResolveActiveSqlAccountName(), cts.Token);

            var resolvedAccount = !string.IsNullOrWhiteSpace(result.AccountName)
                ? result.AccountName
                : Host.ResolveActiveSqlAccountName();
            if (!string.IsNullOrWhiteSpace(resolvedAccount) &&
                Host.ApiKeyVault.ActiveEntry is { } activeEntry &&
                !string.Equals(activeEntry.AccountName, resolvedAccount, StringComparison.OrdinalIgnoreCase))
            {
                Host.UpdateVaultEntry(activeEntry.Id, e => e with
                {
                    AccountName = resolvedAccount,
                    Status = "VALID",
                    LastWalletCopper = result.LatestSnapshot.WalletCopper,
                    LastVerifiedAt = DateTimeOffset.UtcNow
                });
                Host.HasVerifiedApiKey = true;
                Host.RebuildApiKeyViews();
            }

            ArchivedTransactions = await Host.LedgerStore.GetTransactionsAsync(resolvedAccount);
            _latestEquitySnapshot = result.LatestSnapshot;
            _equitySnapshots = await Host.LedgerStore.GetEquitySnapshotsAsync(null, resolvedAccount);
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

            Host.HasVerifiedApiKey = true;
            syncSucceeded = true;
            RecomputeIncomeDashboard();
            Host.RefreshReadinessChecklist();

            IncomeSyncStatusText.Text =
                $"Synced {resolvedAccount ?? "account"} at {DateTime.Now:T} — +{result.NewlyArchivedTransactions:N0} new fills ({result.TotalArchivedTransactions:N0} total archived).";
            Host.AddLog($"Portfolio Sync ({resolvedAccount ?? "Account"}): +{result.NewlyArchivedTransactions} new fills; equity {MarketScanItem.FormatCoins(result.LatestSnapshot.TotalEquityCopper)}.");
        }
        catch (Exception ex)
        {
            IncomeSyncStatusText.Text = $"Sync failed: {ex.Message}";
            Host.AddLog($"ERROR (Portfolio Sync): {ex.Message}");
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

        return syncSucceeded;
    }

    private void UpdateLosingItemsPanel()
    {
        var guidance = PortfolioProfitAdvisor.BuildGuidance(ArchivedTransactions, Array.Empty<Transaction>(), DateTimeOffset.UtcNow);
        var blacklisted = Host.ListsPage.Blacklist.Select(ItemNameNormalizer.Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _losingItems = (guidance.LosingItems ?? Array.Empty<string>())
            .Where(name => !blacklisted.Contains(ItemNameNormalizer.Normalize(name)))
            .ToArray();
        if (_losingItems.Count == 0)
        {
            LosingItemsPanel.Visibility = Visibility.Collapsed;
            return;
        }

        const int shown = 8;
        var names = string.Join(", ", _losingItems.Take(shown)) + (_losingItems.Count > shown ? $" +{_losingItems.Count - shown} more" : string.Empty);
        LosingItemsText.Text = $"Skipped by the bot (lost money on 2+ flips in the last {PortfolioProfitAdvisor.HistoryWindowDays} days): {names}";
        LosingItemsText.ToolTip = string.Join(Environment.NewLine, _losingItems);
        LosingItemsPanel.Visibility = Visibility.Visible;
    }

    private void BlacklistLosingItems_Click(object sender, RoutedEventArgs e)
    {
        var items = _losingItems.ToArray();
        foreach (var name in items)
        {
            Host.ListsPage.AddBlacklistEntry(name);
        }

        UpdateLosingItemsPanel();
        IncomeSyncStatusText.Text = $"Blacklisted {items.Length} item(s) that kept losing money.";
        Host.AddLog($"Blacklisted losing items: {string.Join(", ", items)}");
    }

    private void IncomeTimeframe_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Loading) return;
        RecomputeIncomeDashboard();
    }

    private void IncomeSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (Loading) return;
        ApplyItemPnlSearchFilter();
    }

    private void BlacklistSelectedPnlItem_Click(object sender, RoutedEventArgs e)
    {
        if (ItemPnlGrid.SelectedItem is not ItemPnlRow selected)
        {
            IncomeSyncStatusText.Text = "Select an item row in the Item P&L table first.";
            return;
        }

        Host.ListsPage.AddBlacklistEntry(selected.ItemName);
        IncomeSyncStatusText.Text = $"Added '{selected.ItemName}' to Blacklist.";
        Host.AddLog($"Blacklisted '{selected.ItemName}' from Income Leaderboard.");
    }

    private void ExportLedgerCsv_Click(object sender, RoutedEventArgs e)
    {
        if (ArchivedTransactions.Count == 0)
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
        if (dialog.ShowDialog(Host) != true) return;

        TradingIncomeAnalytics.ExportLedgerCsv(dialog.FileName, ArchivedTransactions);
        IncomeSyncStatusText.Text = $"Exported {ArchivedTransactions.Count:N0} transactions to {Path.GetFileName(dialog.FileName)}.";
        Host.AddLog($"Exported synced ledger ({ArchivedTransactions.Count} rows) to {dialog.FileName}.");
    }

    private void RecomputeIncomeDashboard()
    {
        var windowStart = ResolveSelectedTimeframeStartUtc();
        var report = TradingIncomeAnalytics.Analyze(ArchivedTransactions, windowStart);
        _lastIncomeReport = report;
        _allTimeframePnlRows = report.ItemLeaderboard;

        var nowUtc = DateTimeOffset.UtcNow;
        IncomeMatchedProfitText.Text = MarketScanItem.FormatCoins(report.MatchedProfitCopper);
        var fees = IncomeInsights.FeesPaid(ArchivedTransactions, windowStart);
        IncomeMatchedSubText.Text =
            $"ROI {report.MatchedRoiPercent:0.0}% · {report.MatchedUnitsSold:N0} sold · fees {MarketScanItem.FormatCoins(fees)}";
        IncomeMatchedSubText.ToolTip = $"Return on cost {report.MatchedRoiPercent:0.0}%, {report.MatchedUnitsSold:N0} units sold, {MarketScanItem.FormatCoins(fees)} paid in listing and exchange fees.";
        IncomeProfitNoteText.Text = report.UnmatchedSellRevenueCopper > 0
            ? $"Not counted: {MarketScanItem.FormatCoins(report.UnmatchedSellRevenueCopper)} of sales with no recorded buy"
            : string.Empty;

        if (_latestEquitySnapshot is { } snap)
        {
            IncomeTotalEquityText.Text = MarketScanItem.FormatCoins(snap.TotalEquityCopper);
            if (IncomeInsights.NetWorthChange(_equitySnapshots, windowStart) is { } change)
            {
                var delta = change.End.ValueCopper - change.Start.ValueCopper;
                IncomeNetWorthChangeText.Text =
                    $"{(delta >= 0 ? "+" : "−")}{MarketScanItem.FormatCoins(Math.Abs(delta))} since {change.Start.At.ToLocalTime():MMM d, HH:mm}";
                IncomeNetWorthChangeText.Foreground = delta >= 0
                    ? ResolveBrush("SuccessBrush", Brushes.SeaGreen)
                    : ResolveBrush("DangerBrush", Brushes.IndianRed);
            }
            else
            {
                IncomeNetWorthChangeText.Text = "Change shows after the next sync";
                IncomeNetWorthChangeText.ClearValue(TextBlock.ForegroundProperty);
            }

            var deliveryTotal = snap.DeliveryCopper + snap.DeliveryItemsCopper;
            IncomeEquityBreakdownText.Text =
                $"Wallet {MarketScanItem.FormatCoins(snap.WalletCopper)} • Buy orders {MarketScanItem.FormatCoins(snap.ActiveBuysCopper)} • " +
                $"Listings {MarketScanItem.FormatCoins(snap.ActiveSellsNetCopper)}" +
                (deliveryTotal > 0 ? $" • Delivery {MarketScanItem.FormatCoins(deliveryTotal)}" : string.Empty);
            IncomeEquityBreakdownText.ToolTip = IncomeEquityBreakdownText.Text;

            var share = snap.TotalEquityCopper > 0 ? snap.ActiveBuysCopper * 100.0 / snap.TotalEquityCopper : 0;
            IncomeOrdersHealthText.Text = MarketScanItem.FormatCoins(snap.ActiveBuysCopper);
            IncomeOrdersSubText.Text = $"{share:0}% of net worth • {_openBuyCount} orders • {_outbidBuyCount} outbid (free to re-place)";
        }
        else
        {
            IncomeTotalEquityText.Text = "—";
            IncomeNetWorthChangeText.Text = "Sync to start tracking";
            IncomeEquityBreakdownText.Text = string.Empty;
            IncomeOrdersHealthText.Text = "—";
            IncomeOrdersSubText.Text = $"{_openBuyCount} orders • {_outbidBuyCount} outbid";
        }

        var shelf = IncomeInsights.Shelf(_latestActiveOrders, ArchivedTransactions, nowUtc);
        IncomeDeliveryText.Text = shelf.Listings == 0 ? "—" : MarketScanItem.FormatCoins(shelf.ListedNetCopper);
        IncomeUnmatchedText.Text = shelf.Listings == 0
            ? "No listings"
            : $"{shelf.Listings} listings • {shelf.Undercut} undercut" + (shelf.UndercutOver12h > 0 ? $" ({shelf.UndercutOver12h} for 12 h+)" : string.Empty);
        IncomeShelfNoteText.Text = shelf.UnsoldCostCopper > 0
            ? $"Bought, not sold yet: {MarketScanItem.FormatCoins(shelf.UnsoldCostCopper)} at cost"
            : string.Empty;

        var insight = PortfolioProfitAdvisor.BuildDashboardInsightSummary(report, _latestActiveOrders, nowUtc);
        PortfolioInsightsText.Text = shelf.UndercutOver12h > 0
            ? $"{insight}  •  {shelf.UndercutOver12h} listing(s) undercut for 12 h+"
            : insight;
        UpdateLosingItemsPanel();

        ApplyItemPnlSearchFilter();

        _ledgerRows.Clear();
        var filteredLedger = ArchivedTransactions
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
        var chartMode = (IncomeChartModeSelector.SelectedItem as ComboBoxItem)?.Tag as string;
        if (chartMode == "DailyProfit")
        {
            RebuildDailyProfitChart(report, successBrush, dangerBrush, mutedBrush);
        }
        else if (chartMode != "Hourly")
        {
            RebuildNetWorthChart(accentBrush);
        }

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

        if (chartMode == "Hourly")
        {
            foreach (var bar in _hourlyChartBars) _dailyChartBars.Add(bar);
            DailyChartSummaryText.Text = HourlyChartSummaryText.Text;
        }
    }

    private void IncomeChartMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Loading || _lastIncomeReport is null) return;
        RebuildIncomeCharts(_lastIncomeReport);
    }

    /// <summary>Net worth bars scaled between the period's low and high, so a 10% move is visible.</summary>
    private void RebuildNetWorthChart(Brush accentBrush)
    {
        var points = IncomeInsights.NetWorthSeries(_equitySnapshots, ResolveSelectedTimeframeStartUtc());
        if (points.Count == 0)
        {
            DailyChartSummaryText.Text = "Sync the account to start tracking";
            return;
        }

        var low = points.Min(p => p.ValueCopper);
        var high = points.Max(p => p.ValueCopper);
        var range = Math.Max(1L, high - low);
        var labelEvery = Math.Max(1, (int)Math.Ceiling(points.Count / 8.0));
        for (var i = 0; i < points.Count; i++)
        {
            var point = points[i];
            var height = 14.0 + (point.ValueCopper - low) / (double)range * 70.0;
            var tooltip = $"{point.At.ToLocalTime():g}: {MarketScanItem.FormatCoins(point.ValueCopper)}";
            _dailyChartBars.Add(new ChartBarItem(i % labelEvery == 0 ? point.Label : string.Empty, height, accentBrush, tooltip, MarketScanItem.FormatCoins(point.ValueCopper)));
        }

        var delta = points[^1].ValueCopper - points[0].ValueCopper;
        DailyChartSummaryText.Text = $"{(delta >= 0 ? "+" : "−")}{MarketScanItem.FormatCoins(Math.Abs(delta))} • low {MarketScanItem.FormatCoins(low)} • high {MarketScanItem.FormatCoins(high)}";
    }

    private void RebuildDailyProfitChart(TradingIncomeReport report, Brush successBrush, Brush dangerBrush, Brush mutedBrush)
    {
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
        public string AgeFormatted => IncomeInsights.FormatAge(DateTimeOffset.UtcNow - order.CreatedAt);
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
}
