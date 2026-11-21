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

/// <summary>What runs when Mmopal opens: account sync, Auto-Tune and market scan, and the Startup strip.</summary>
public partial class SessionPage
{
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
            var synced = await Host.IncomePage.SyncPortfolioCoreAsync();
            var syncText = Host.IncomePage.LatestEquitySnapshot is { } snap
                ? $"Account synced ({MarketScanItem.FormatCoins(snap.WalletCopper)} wallet)"
                : "Account sync failed";
            SetLaunchStep(LaunchSyncText, synced ? LaunchStep.Done : LaunchStep.Failed, syncText);
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

    private static string FormatAge(TimeSpan age)
    {
        if (age == TimeSpan.MaxValue)
        {
            return "missing";
        }

        return age.TotalDays >= 1 ? $"{age.TotalDays:0.#} days" : $"{age.TotalHours:0.#} h";
    }
}
