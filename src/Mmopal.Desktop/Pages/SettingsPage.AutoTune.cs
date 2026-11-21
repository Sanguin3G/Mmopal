using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Mmopal.Contracts;
using Mmopal.Gw2;
using Mmopal.Infrastructure;
using static Mmopal.Desktop.UiHelpers;

namespace Mmopal.Desktop.Pages;

/// <summary>Auto-Tune: sizing and learning from results, keeping values set by hand, and re-tuning before a session.</summary>
public partial class SettingsPage
{
    private async void AutoTuneForWallet_Click(object sender, RoutedEventArgs e) => await AutoTuneForWalletAsync(sender as Button);

    internal async Task AutoTuneForWalletAsync(Button? sender)
    {
        // Clicking Auto-Tune is an explicit "decide for me": hand-set values go back to Auto-Tune. The automatic runs
        // (launch, Start session) keep them.
        if (_autoTuneKeeps.Count > 0)
        {
            Host.AddLog($"Auto-Tune button: your hand-set values ({DescribeAutoTuneKeeps()}) are handed back to Auto-Tune.");
            _autoTuneKeeps.Clear();
            UpdateAutoTuneKeepsHint();
        }

        await RunAutoTuneAsync(sender ?? AutoTuneSettingsButton);
    }

    internal async Task<AutoTuneResult> RunAutoTuneAsync(Button? feedbackButton)
    {
        long capitalCopper = Host.IncomePage.LatestEquitySnapshot?.WalletCopper ?? 0;
        var sourceLabel = "cached snapshot";

        var key = Host.ResolveConfiguredApiKey();
        if (!string.IsNullOrWhiteSpace(key))
        {
            try
            {
                using var cts = Host.CreateApiCancellationTokenSource();
                var provider = new Gw2OfficialDataProvider(Host.Gw2HttpClient, key);
                var balance = await provider.GetBalanceAsync(cts.Token);
                if (balance is not null)
                {
                    capitalCopper = balance.Copper;
                    sourceLabel = "live GW2 wallet";
                    Host.HasVerifiedApiKey = true;
                }
            }
            catch (Exception ex)
            {
                Host.AddLog($"Auto-tune wallet fetch note: {ex.Message}");
            }
        }

        if (capitalCopper <= 0)
        {
            capitalCopper = 5_000_000; // 500g balanced default when offline
            sourceLabel = "default 500g balanced baseline (no API wallet yet)";
        }

        var tune = ApplySmartAutoTune(capitalCopper);
        var message = $"Auto-tuned from {sourceLabel}: {tune.Summary}";
        Host.SessionPage.StatusText.Text = message;
        Host.ScannerPage.ScanStatusText.Text = message;
        Host.AddLog($"Auto-Tune ({sourceLabel}): {tune.Summary}");
        if (feedbackButton is not null)
        {
            await FlashButtonFeedbackAsync(feedbackButton, "✓ Tuned");
        }

        return tune;
    }

    /// <summary>Smart Auto-Tune: capital (wallet + gold in orders) plus the last 14 days of your own flips.</summary>
    private AutoTuneResult ApplySmartAutoTune(long walletCopper)
    {
        var snapshot = Host.IncomePage.LatestEquitySnapshot;
        var recent = Host.IncomePage.ArchivedTransactions.Count == 0
            ? null
            : TradingIncomeAnalytics.Analyze(Host.IncomePage.ArchivedTransactions, DateTimeOffset.UtcNow.AddDays(-SmartAutoTuner.ResultsWindowDays));
        var tune = SmartAutoTuner.Tune(new AutoTuneInputs(
            walletCopper,
            snapshot?.ActiveBuysCopper ?? 0,
            snapshot?.ActiveSellsNetCopper ?? 0,
            recent));
        Host.ScannerPage.ApplyStrategyPresetToScanner(tune.Preset);
        ApplyStrategyPresetToSettings(tune.Preset);
        Host.ScannerPage.ScanMinTradesPerDayBox.Text = tune.MinTradesPerDay.ToString(CultureInfo.CurrentCulture);
        _lastAutoTunedPreset = tune.Preset.Name;
        SaveAllSettingsSilently();
        Host.RefreshReadinessChecklist();
        if (_autoTuneKeeps.Count > 0)
        {
            tune = tune with { Summary = $"{tune.Summary} Kept your own: {DescribeAutoTuneKeeps()}." };
        }

        var tip = $"Last Auto-Tune ({DateTime.Now:g}): {tune.Summary}";
        Host.ScannerPage.AutoTuneScannerButton.ToolTip = tip;
        AutoTuneSettingsButton.ToolTip = tip;
        return tune;
    }

    /// <summary>Re-tune right before a session from the last synced wallet and orders (no network call).</summary>
    internal void TuneBeforeSession()
    {
        if (AutoTuneEachSessionCheckBox.IsChecked != true || Host.IncomePage.LatestEquitySnapshot is not { WalletCopper: > 0 } snapshot)
        {
            return;
        }

        var tune = ApplySmartAutoTune(snapshot.WalletCopper);
        Host.AddLog($"Auto-Tune before session (gold as of the last account sync): {tune.Summary}");
    }

    private void TunedField_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (Loading || _applyingAutoTune || sender is not TextBox { Tag: string key } || !_autoTuneKeeps.Add(key))
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
        Host.AddLog("Auto-Tune will set every trading value again from the next launch or Start session.");
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
}
