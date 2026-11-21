using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Mmopal.Contracts;
using Mmopal.Gw2;
using static Mmopal.Desktop.UiHelpers;

namespace Mmopal.Desktop.Pages;

/// <summary>Market Scanner: finds profitable flips from the GW2 API and hands them to the session.</summary>
public partial class ScannerPage : UserControl
{
    private readonly ObservableCollection<MarketScanItem> _scanResults = new();
    private CancellationTokenSource? _scanCancellation;
    private MainWindow? _host;

    public ScannerPage()
    {
        InitializeComponent();
        ScanResultsGrid.ItemsSource = _scanResults;
    }

    private MainWindow Host => _host ?? throw new InvalidOperationException("The page is not attached to the main window.");

    private bool Loading => _host is null || _host.IsLoadingUi;

    internal void Attach(MainWindow host) => _host = host;

    internal ObservableCollection<MarketScanItem> ScanResults => _scanResults;

    /// <summary>Stops a running scan when the window closes.</summary>
    internal void CancelRunningScan()
    {
        _scanCancellation?.Cancel();
        _scanCancellation?.Dispose();
    }

    private async void AutoTuneForWallet_Click(object sender, RoutedEventArgs e) => await Host.SettingsPage.AutoTuneForWalletAsync(sender as Button);

    /// <summary>Labels each scan row with what your own last 30 days of trades say about it.</summary>
    private IReadOnlyList<MarketScanItem> AnnotateWithHistory(IReadOnlyList<MarketScanItem> items)
    {
        if (Host.IncomePage.ArchivedTransactions.Count == 0)
        {
            return items;
        }

        var guidance = PortfolioProfitAdvisor.BuildGuidance(Host.IncomePage.ArchivedTransactions, Array.Empty<Transaction>(), DateTimeOffset.UtcNow);
        return items
            .Select(item => guidance.TryGetAdvice(item.ItemId, item.ItemName) is { } advice ? item with { HistoryNote = advice.ShortLabel } : item)
            .ToArray();
    }

    private void ApplyScannerPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.CommandParameter is not string presetName) return;
        var preset = TradingStrategyPresets.FindByName(presetName);
        if (preset is null) return;

        ApplyStrategyPresetToScanner(preset);
        ScanStatusText.Text = $"Applied '{preset.Name}' preset: {preset.Rationale}";
        Host.AddLog($"Scanner Preset '{preset.Name}': {preset.Description}");
    }

    internal void ApplyStrategyPresetToScanner(TradingStrategyPreset preset)
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

    private void ScanMarket_Click(object sender, RoutedEventArgs e) => RunMarketScanAsync(useCachedPrices: false);

    private void ReFilterCachedScan_Click(object sender, RoutedEventArgs e) => RunMarketScanAsync(useCachedPrices: true);

    private async void RunMarketScanAsync(bool useCachedPrices) => await RunMarketScanCoreAsync(useCachedPrices);

    internal async Task<bool> RunMarketScanCoreAsync(bool useCachedPrices)
    {
        if (!TryReadScanFilter(out var filter, out var error))
        {
            ScanStatusText.Text = error ?? "Invalid scanner filter.";
            return false;
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
            var blacklistSnapshot = Host.ListsPage.Blacklist.ToArray();
            IReadOnlyList<MarketScanItem> items;
            if (useCachedPrices && Host.MarketScanner is Gw2MarketScanner concreteScanner && concreteScanner.HasCachedScan)
            {
                items = await concreteScanner.ReFilterCachedAsync(filter, blacklistSnapshot, progress, ct);
            }
            else
            {
                items = await Host.MarketScanner.ScanAsync(filter, blacklistSnapshot, progress, ct);
            }

            items = AnnotateWithHistory(items);
            _scanResults.Clear();
            foreach (var item in items)
            {
                _scanResults.Add(item);
            }

            ScanProgressBar.Value = 100;
            var spikeNote = Host.MarketScanner is Gw2MarketScanner { LastSpikesSkipped: > 0 } spikeScanner
                ? $" Skipped {spikeScanner.LastSpikesSkipped} price spike(s) vs the 7-day average."
                : string.Empty;
            ScanStatusText.Text = $"Found {items.Count} profitable items matching your filters.{spikeNote}";
            Host.AddLog($"Market Scanner: Found {items.Count} profitable items.{spikeNote}");
            Host.SettingsPage.SaveOperatorSettingsIfValid();
            return true;
        }
        catch (OperationCanceledException)
        {
            ScanStatusText.Text = "Market scan cancelled.";
        }
        catch (Exception exception)
        {
            ScanStatusText.Text = $"Scan failed: {exception.Message}";
            Host.AddLog($"ERROR (Market Scanner): {exception.Message}");
        }
        finally
        {
            ScanMarketButton.IsEnabled = true;
            ReFilterButton.IsEnabled = true;
            CancelScanButton.IsEnabled = false;
        }

        return false;
    }

    private void CancelScan_Click(object sender, RoutedEventArgs e) => _scanCancellation?.Cancel();

    private async void ImportScannerFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import GW2BLTC / GW2Profits CSV or JSON",
            Filter = "Supported Lists (*.csv;*.json)|*.csv;*.json|All Files (*.*)|*.*"
        };
        if (dialog.ShowDialog(Host) != true) return;

        try
        {
            var imported = await Gw2MarketScanner.ImportFromFileAsync(dialog.FileName);
            _scanResults.Clear();
            foreach (var item in imported)
            {
                _scanResults.Add(item);
            }

            ScanStatusText.Text = $"Imported {imported.Count} items from {Path.GetFileName(dialog.FileName)}.";
            Host.AddLog($"Imported {imported.Count} items into Market Scanner from {dialog.FileName}.");
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

        BindScanResultsToSession();
        Host.AddLog($"Bound {_scanResults.Count} scanned items to session candidates.");
        await FlashButtonFeedbackAsync(UseScanResultsButton, "✓ Sent to Session");
    }

    internal void BindScanResultsToSession()
    {
        var path = MainWindow.ScannedCandidatesPath;
        OperatorListsStore.SaveShoppingList(path, _scanResults.Select(item => item.ToCandidate()));
        Host.SettingsPage.CandidatesPathBox.Text = path;
        Host.SettingsPage.SaveOperatorSettingsIfValid();
        Host.RefreshReadinessChecklist();
        ScanStatusText.Text = $"Saved {_scanResults.Count} items to session candidates ({Path.GetFileName(path)}).";
    }

    private void AddSelectedScanToCustomList_Click(object sender, RoutedEventArgs e)
    {
        if (ScanResultsGrid.SelectedItem is not MarketScanItem selected)
        {
            ScanStatusText.Text = "Select an item in the table first.";
            return;
        }

        Host.ListsPage.AddOrReplaceCustomCandidate(selected.ToCandidate());
        Host.RefreshReadinessChecklist();
        ScanStatusText.Text = $"Added '{selected.ItemName}' to Custom Shopping List.";
    }

    private void BlacklistSelectedScanItem_Click(object sender, RoutedEventArgs e)
    {
        if (ScanResultsGrid.SelectedItem is not MarketScanItem selected)
        {
            ScanStatusText.Text = "Select an item in the table first.";
            return;
        }

        Host.ListsPage.AddBlacklistEntry(selected.ItemName);
        _scanResults.Remove(selected);
        ScanStatusText.Text = $"Blacklisted '{selected.ItemName}'.";
    }

    internal bool TryReadScanFilter(out MarketScanFilter filter, out string? error)
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
        var defaultQty = long.TryParse(Host.SettingsPage.DefaultQuantityBox.Text.Trim(), out var parsedQty) && parsedQty > 0 ? parsedQty : 1;
        var overbid = int.TryParse(Host.SettingsPage.OverbidMinBox.Text.Trim(), out var parsedOb) && parsedOb >= 0 ? parsedOb : 0;
        var undercut = int.TryParse(Host.SettingsPage.UndercutMinBox.Text.Trim(), out var parsedUc) && parsedUc >= 0 ? parsedUc : 0;

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
            MinimumDailyTrades: minTradesPerDay,
            SkipPriceSpikes: ScanSkipSpikesCheckBox.IsChecked == true);
        return true;
    }

    internal decimal ReadAutoRescanHours() =>
        decimal.TryParse(AutoRescanHoursBox?.Text?.Trim(), NumberStyles.Number, CultureInfo.CurrentCulture, out var hours) && hours > 0
            ? Math.Min(hours, 24m)
            : 0m;

    internal ScannerUiState CaptureScannerUiState()
    {
        static string? TagOf(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        return new ScannerUiState(
            MinRoi: ScanMinRoiBox.Text.Trim(),
            MaxRoi: ScanMaxRoiBox.Text.Trim(),
            MinProfitSilver: ScanMinProfitSilverBox.Text.Trim(),
            MinBuySilver: ScanMinBuySilverBox.Text.Trim(),
            MaxBuyGold: ScanMaxBuyGoldBox.Text.Trim(),
            MinSupply: ScanMinSupplyBox.Text.Trim(),
            MinDemand: ScanMinDemandBox.Text.Trim(),
            MinTradesPerDay: ScanMinTradesPerDayBox.Text.Trim(),
            Rarity: TagOf(ScanRaritySelector),
            Category: TagOf(ScanCategorySelector),
            Sort: TagOf(ScanSortSelector),
            Scope: TagOf(ScanScopeSelector),
            CheckWalls: ScanCheckWallsCheckBox.IsChecked == true,
            ShowTradesPerDay: ScanVelocityCheckBox.IsChecked == true,
            SkipPriceSpikes: ScanSkipSpikesCheckBox.IsChecked == true);
    }

    internal void ApplyScannerUiState(ScannerUiState? state)
    {
        if (state is null)
        {
            return;
        }

        ScanMinRoiBox.Text = state.MinRoi;
        ScanMaxRoiBox.Text = state.MaxRoi;
        ScanMinProfitSilverBox.Text = state.MinProfitSilver;
        ScanMinBuySilverBox.Text = state.MinBuySilver;
        ScanMaxBuyGoldBox.Text = state.MaxBuyGold;
        ScanMinSupplyBox.Text = state.MinSupply;
        ScanMinDemandBox.Text = state.MinDemand;
        ScanMinTradesPerDayBox.Text = state.MinTradesPerDay;
        if (state.Rarity is not null) SelectComboByTag(ScanRaritySelector, state.Rarity);
        if (state.Category is not null) SelectComboByTag(ScanCategorySelector, state.Category);
        if (state.Sort is not null) SelectComboByTag(ScanSortSelector, state.Sort);
        if (state.Scope is not null) SelectComboByTag(ScanScopeSelector, state.Scope);
        ScanCheckWallsCheckBox.IsChecked = state.CheckWalls;
        ScanSkipSpikesCheckBox.IsChecked = state.SkipPriceSpikes;
    }
}
