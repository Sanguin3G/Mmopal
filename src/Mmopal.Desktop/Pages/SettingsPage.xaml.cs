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

/// <summary>Settings: API keys, GW2 client, latency, trading values with Auto-Tune, schedule, hotkeys and reports.</summary>
public partial class SettingsPage : UserControl
{
    private static readonly IReadOnlyDictionary<string, string> AutoTuneFieldLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["MinProfit"] = "Minimum profit %",
        ["MaxBuy"] = "Max buy price",
        ["Quantity"] = "Default quantity",
        ["GoldFloor"] = "Gold floor",
        ["Overbid"] = "Overbid",
        ["Undercut"] = "Undercut"
    };

    private readonly ObservableCollection<ApiKeyRowViewModel> _apiKeyRows = new();
    private string? _lastAutoTunedPreset;
    // Auto-Tune fields the operator set by hand; Auto-Tune keeps them (the 25g gold floor was reset to 80g before).
    private readonly HashSet<string> _autoTuneKeeps = new(StringComparer.OrdinalIgnoreCase);
    private bool _applyingAutoTune;
    private MainWindow? _host;

    public SettingsPage()
    {
        InitializeComponent();
        ApiKeysGrid.ItemsSource = _apiKeyRows;
    }

    private MainWindow Host => _host ?? throw new InvalidOperationException("The page is not attached to the main window.");

    private bool Loading => _host is null || _host.IsLoadingUi;

    internal void Attach(MainWindow host) => _host = host;

    internal string? LastAutoTunedPreset => _lastAutoTunedPreset;

    private void LaunchPlatformSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Loading) return;
        var platformTag = GetSelectedLaunchPlatform();
        if (Host.ApiKeyVault.ActiveEntry is { } active &&
            !string.Equals(active.LaunchPlatform, platformTag, StringComparison.OrdinalIgnoreCase))
        {
            Host.UpdateVaultEntry(active.Id, entry => entry with { LaunchPlatform = platformTag });
            Host.RebuildApiKeyViews();
        }

        Host.SessionPage.UpdateSessionPaceSummary();
        Host.RefreshReadinessChecklist();
        SaveAllSettingsSilently();
    }

    internal string GetSelectedLaunchPlatform() =>
        (LaunchPlatformSelector?.SelectedItem as ComboBoxItem)?.Tag?.ToString() switch
        {
            var tag when string.Equals(tag, Gw2ClientSettings.PlatformSteam, StringComparison.OrdinalIgnoreCase) => Gw2ClientSettings.PlatformSteam,
            _ => Gw2ClientSettings.PlatformArenaNet
        };

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
            Host.AddLog($"Could not open browser: {ex.Message}");
        }
    }

    private async void UseApiKey_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.CommandParameter is not string keyId) return;
        await Host.SwitchActiveAccountAsync(keyId);
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
        var entry = Host.ApiKeyVault.Entries.FirstOrDefault(item => string.Equals(item.Id, keyId, StringComparison.OrdinalIgnoreCase));
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
        await Host.RemoveVaultEntryAsync(keyId);
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
        var entry = Host.ApiKeyVault.Entries.FirstOrDefault(e => string.Equals(e.Id, row.Id, StringComparison.OrdinalIgnoreCase));
        if (entry is null || string.Equals(entry.CustomName ?? string.Empty, name, StringComparison.Ordinal))
        {
            return;
        }

        row.CustomName = name;
        Host.ApiKeyVault = Host.ApiKeyVault with
        {
            Entries = Host.ApiKeyVault.Entries
                .Select(e => string.Equals(e.Id, row.Id, StringComparison.OrdinalIgnoreCase) ? e with { CustomName = name } : e)
                .ToArray()
        };
        Host.SaveApiKeyVault(Host.ApiKeyVault);
        ApiKeyStatusText.Text = string.IsNullOrEmpty(name)
            ? $"Removed the name of {entry.AccountName}."
            : $"Saved the name '{name}' for {entry.AccountName}.";
        // Refresh the account picker and labels after the edit, not during it.
        Dispatcher.BeginInvoke(Host.RebuildApiKeyViews, System.Windows.Threading.DispatcherPriority.Background);
    }

    private async void ReverifyApiKeys_Click(object sender, RoutedEventArgs e)
    {
        if (Host.ApiKeyVault.Entries.Count == 0)
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
            var refreshed = new List<Gw2ApiKeyEntry>(Host.ApiKeyVault.Entries.Count);
            foreach (var entry in Host.ApiKeyVault.Entries)
            {
                try
                {
                    using var cts = Host.CreateApiCancellationTokenSource();
                    var provider = new Gw2OfficialDataProvider(Host.Gw2HttpClient, entry.ApiKey);
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

            Host.ApiKeyVault = Host.ApiKeyVault with { Entries = refreshed.ToArray() };
            Host.SaveApiKeyVault(Host.ApiKeyVault);
            Host.HasVerifiedApiKey = Host.ApiKeyVault.ActiveEntry?.Status.Equals("VALID", StringComparison.OrdinalIgnoreCase) == true;
            Host.RebuildApiKeyViews();
            Host.RefreshReadinessChecklist();
            ApiKeyStatusText.Text = $"Re-verified {Host.ApiKeyVault.Entries.Count} key(s): {verifiedCount} valid.";
            Host.AddLog($"Re-verified API vault ({verifiedCount}/{Host.ApiKeyVault.Entries.Count} valid).");
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
            var existing = Host.ApiKeyVault.Entries.FirstOrDefault(item => string.Equals(item.ApiKey, raw, StringComparison.OrdinalIgnoreCase));
            var entryId = existing?.Id ?? Guid.NewGuid().ToString("N");
            var accountName = existing?.AccountName ?? "Default Account";
            var status = "UNVERIFIED";
            IReadOnlyList<string> permissions = existing?.Permissions ?? Array.Empty<string>();
            long? walletCopper = existing?.LastWalletCopper;
            DateTimeOffset? verifiedAt = existing?.LastVerifiedAt;
            var statusDetail = "Encrypted with Windows DPAPI (CurrentUser) and stored in multi-account vault.";

            try
            {
                using var cts = Host.CreateApiCancellationTokenSource();
                var provider = new Gw2OfficialDataProvider(Host.Gw2HttpClient, raw);
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
                    Host.HasVerifiedApiKey = true;
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

            var updatedEntries = Host.ApiKeyVault.Entries
                .Select(item => string.Equals(item.Id, entryId, StringComparison.OrdinalIgnoreCase) ? newEntry : item)
                .ToList();
            if (existing is null)
            {
                updatedEntries.Add(newEntry);
            }

            Host.ApiKeyVault = new Gw2ApiKeyVaultData(entryId, updatedEntries.ToArray());
            Host.SaveApiKeyVault(Host.ApiKeyVault);

            ApiKeyPasswordBox.Clear();
            ApiKeyCustomNameBox.Clear();
            Host.RebuildApiKeyViews();
            Host.RefreshReadinessChecklist();
            ApiKeyStatusText.Text = statusDetail;
            Host.AddLog($"Saved DPAPI API key for {newEntry.DisplayLabel} ({status}).");
            saved = true;

            await Host.IncomePage.ReloadLedgerForActiveAccountAsync();
            await Host.ReportsPage.LoadSessionReportsAsync();
            if (Host.Supervisor is DesktopWorkerSupervisor concreteSupervisor && Host.LastWorkerState is WorkerState.Idle or WorkerState.Stopped)
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
        var activeEntry = Host.ApiKeyVault.ActiveEntry;
        var key = typedKey ?? Host.ResolveConfiguredApiKey();

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
            using var cts = Host.CreateApiCancellationTokenSource();
            var provider = new Gw2OfficialDataProvider(Host.Gw2HttpClient, key);
            var result = await provider.ValidateApiKeyAsync(cts.Token);
            isValid = result.IsValid;

            if (typedKey is null && activeEntry is not null)
            {
                Host.UpdateVaultEntry(activeEntry.Id, item => item with
                {
                    AccountName = !string.IsNullOrWhiteSpace(result.AccountName) ? result.AccountName! : item.AccountName,
                    Status = result.IsValid ? "VALID" : "INVALID",
                    Permissions = result.Permissions,
                    LastWalletCopper = result.CoinCopper ?? item.LastWalletCopper,
                    LastVerifiedAt = DateTimeOffset.UtcNow
                });
                Host.RebuildApiKeyViews();
            }

            if (result.IsValid)
            {
                Host.HasVerifiedApiKey = true;
                var coinText = result.CoinCopper.HasValue ? $" • Wallet: {MarketScanItem.FormatCoins(result.CoinCopper.Value)}" : string.Empty;
                var accountText = !string.IsNullOrWhiteSpace(result.AccountName) ? $"{result.AccountName}" : "Account";
                ApiKeyStatusText.Text = $"{accountText}{coinText} — {result.Message}";
                Host.AddLog($"API key verified: {accountText}{coinText}");
            }
            else
            {
                Host.HasVerifiedApiKey = false;
                ApiKeyStatusText.Text = $"API key test failed: {result.Message}";
            }

            Host.RefreshReadinessChecklist();
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
        if (Host.ApiKeyVault.ActiveEntry is { } active)
        {
            await Host.RemoveVaultEntryAsync(active.Id);
            return;
        }

        Host.HasVerifiedApiKey = false;
        Host.RebuildApiKeyViews();
        Host.RefreshReadinessChecklist();
        Host.AddLog("Cleared stored DPAPI GW2 API key.");
    }

    private void LatencyProfileSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Loading || LatencyProfileSelector.SelectedItem is not ComboBoxItem { Tag: string profileId })
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
        var candidates = Host.DiscoverGw2Installs();
        var detected = candidates.FirstOrDefault()?.ExecutablePath ?? Host.DetectGw2Path();
        var dpi = Host.DetectDisplayScale();

        var found = !string.IsNullOrWhiteSpace(detected);
        if (found)
        {
            Gw2ExecutablePathBox.Text = detected;
            var kindText = candidates.FirstOrDefault() is { } first
                ? $"{first.Kind} install via {first.Source}"
                : "Detected GW2 executable";
            Gw2DetectHealthText.Text = $"{kindText}  •  Display Scale: {dpi.Summary}";
            Host.SessionPage.StatusText.Text = $"Detected Guild Wars 2 at {detected}";
            Host.AddLog($"Detected GW2 ({kindText}): {detected}");
        }
        else
        {
            Gw2DetectHealthText.Text = $"Could not auto-detect Gw2-64.exe  •  Display Scale: {dpi.Summary}";
            Host.SessionPage.StatusText.Text = "Could not auto-detect Gw2-64.exe; click Browse or enter steam://rungameid/1284210.";
        }

        Host.RefreshReadinessChecklist();
        await FlashButtonFeedbackAsync(DetectGw2Button, found ? "✓ Detected" : "Not found");
    }

    private void BrowseGw2_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select Gw2-64.exe",
            Filter = "Guild Wars 2 (Gw2-64.exe)|Gw2-64.exe|Executables (*.exe)|*.exe"
        };
        if (dialog.ShowDialog(Host) == true)
        {
            Gw2ExecutablePathBox.Text = dialog.FileName;
            Host.RefreshReadinessChecklist();
        }
    }

    private async void ApplyHotkeys_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadOperatorSettings(out var opSettings)) return;
        Host.SaveOperatorSettings(opSettings);
        Host.RegisterConfiguredHotkeys(logWarnings: true);
        await FlashButtonFeedbackAsync(ApplyHotkeysButton, "✓ Applied");
    }

    private async void SaveAllSettings_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadLimits(out var limits)) return;
        if (!TryReadClientSettings(out var clientSettings)) return;
        if (!TryReadOperatorSettings(out var opSettings)) return;

        Host.SaveLimits(limits);
        Host.SaveClientSettings(clientSettings);
        Host.SaveOperatorSettings(opSettings);
        Host.RegisterConfiguredHotkeys(logWarnings: true);
        Host.RefreshReadinessChecklist();
        Host.SessionPage.StatusText.Text = "All settings saved to local profiles folder.";
        Host.AddLog("Saved trading limits, GW2 client/latency settings, session cap, AFK policy, and hotkeys.");
        await FlashButtonFeedbackAsync(SaveAllSettingsButton, "✓ Saved");
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        var paths = RuntimePaths.Create();
        paths.EnsureDirectories();
        Process.Start(new ProcessStartInfo { FileName = paths.Root, UseShellExecute = true });
    }

    private void MoveApiKeyEntry(string keyId, int delta)
    {
        var list = Host.ApiKeyVault.Entries.ToList();
        var index = list.FindIndex(item => string.Equals(item.Id, keyId, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return;
        var targetIndex = index + delta;
        if (targetIndex < 0 || targetIndex >= list.Count) return;

        (list[index], list[targetIndex]) = (list[targetIndex], list[index]);
        Host.ApiKeyVault = Host.ApiKeyVault with { Entries = list.ToArray() };
        Host.SaveApiKeyVault(Host.ApiKeyVault);
        Host.RebuildApiKeyViews();
    }

    internal void RebuildApiKeyRows()
    {
        var passBrush = ResolveBrush("SuccessBrush", Brushes.SeaGreen);
        var warnBrush = ResolveBrush("WarningBrush", Brushes.DarkOrange);
        var dangerBrush = ResolveBrush("DangerBrush", Brushes.IndianRed);

        var activeId = Host.ApiKeyVault.ActiveEntry?.Id;
        _apiKeyRows.Clear();
        for (var i = 0; i < Host.ApiKeyVault.Entries.Count; i++)
        {
            var entry = Host.ApiKeyVault.Entries[i];
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
                canMoveDown: i < Host.ApiKeyVault.Entries.Count - 1,
                statusUpper,
                statusBrush,
                string.IsNullOrWhiteSpace(entry.AccountName) ? "Default Account" : entry.AccountName,
                string.IsNullOrWhiteSpace(entry.LaunchPlatform) ? "ArenaNet" : entry.LaunchPlatform,
                entry.MaskedKey,
                entry.CustomName));
        }
    }

    internal void RefreshApiKeyIndicator()
    {
        if (Host.ApiKeyVault.ActiveEntry is { } active)
        {
            var countNote = Host.ApiKeyVault.Entries.Count > 1 ? $" ({Host.ApiKeyVault.Entries.Count} accounts saved)" : string.Empty;
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

    internal void LoadLimits()
    {
        var limits = Host.ReadSavedLimits();
        MinimumProfitBox.Text = limits.MinimumProfitPercentage == 0
            ? string.Empty
            : limits.MinimumProfitPercentage.ToString(CultureInfo.CurrentCulture);
        MaximumBuyGoldBox.Text = limits.MaximumBuyPriceGold?.ToString(CultureInfo.CurrentCulture) ?? string.Empty;
    }

    internal void LoadClientSettings()
    {
        var settings = Host.ReadSavedClientSettings();
        var installs = Host.DiscoverGw2Installs();
        var detectedPath = installs.FirstOrDefault()?.ExecutablePath ?? Host.DetectGw2Path();
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

        var dpi = Host.DetectDisplayScale();
        var installInfo = installs.FirstOrDefault() is { } first
            ? $"{first.Kind} ({first.Source})"
            : !string.IsNullOrWhiteSpace(Gw2ExecutablePathBox.Text) ? "Configured path" : "Not detected yet";
        Gw2DetectHealthText.Text = $"Client: {installInfo}  •  Display Scale: {dpi.Summary}";
        Host.SessionPage.SessionCapStatusText.Text = settings.MaxSessionDurationMinutes is > 0
            ? $"Session cap: {settings.FormatMaxSessionHours()}h"
            : "Session cap: disabled";
    }

    internal void LoadOperatorSettings()
    {
        var settings = Host.ReadSavedOperatorSettings();
        SelectComboByContent(Host.ThemeSelector, settings.ThemeMode);
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
        Host.ScannerPage.ScanVelocityCheckBox.IsChecked = settings.EnableDataWarsVelocity;
        Host.SessionPage.AutoSellInventoryCheckBox.IsChecked = settings.AutoListBoughtFlipItems;
        Host.ScannerPage.AutoRescanHoursBox.Text = settings.AutoRescanHours.ToString(CultureInfo.CurrentCulture);
        Host.ScannerPage.ApplyScannerUiState(settings.Scanner);
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
        Host.ReportsPage.UpdateReportRulesSummary(settings.ToSessionReportPolicy());
    }

    internal bool TryReadLimits(out TradingLimits limits)
    {
        if (TradingLimits.TryParse(MinimumProfitBox.Text, MaximumBuyGoldBox.Text, out limits, out var error))
        {
            return true;
        }

        Host.SessionPage.StatusText.Text = error ?? "Trading limits are invalid.";
        return false;
    }

    internal bool TryReadClientSettings(out Gw2ClientSettings settings)
    {
        settings = new Gw2ClientSettings();
        if (!Gw2ClientSettings.TryParseSwapHours(SwapCharacterHoursBox.Text, out var swapMinutes, out var error))
        {
            Host.SessionPage.StatusText.Text = error ?? "Character swap interval is invalid.";
            return false;
        }

        if (!Gw2ClientSettings.TryParseMaxSessionHours(MaxSessionHoursBox.Text, out var maxSessionMinutes, out var capError))
        {
            Host.SessionPage.StatusText.Text = capError ?? "Max session duration is invalid.";
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
        Host.SessionPage.SessionCapStatusText.Text = settings.MaxSessionDurationMinutes is > 0
            ? $"Session cap: {settings.FormatMaxSessionHours()}h"
            : "Session cap: disabled";
        return true;
    }

    internal bool TryReadOperatorSettings(out OperatorSettings settings)
    {
        var theme = (Host.ThemeSelector.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "System";
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
            EnableDataWarsVelocity: Host.ScannerPage.ScanVelocityCheckBox.IsChecked == true,
            AutoListBoughtFlipItems: Host.SessionPage.AutoSellInventoryCheckBox.IsChecked == true,
            AutoRescanHours: Host.ScannerPage.ReadAutoRescanHours(),
            AutoDockSideBySideWhenRunning: AutoDockBesideGw2CheckBox.IsChecked == true,
            UsePortfolioIntelligence: UsePortfolioIntelligenceCheckBox.IsChecked == true,
            SaveSessionReports: SaveSessionReportsCheckBox.IsChecked == true,
            ReportIncludeDryRunAndObserve: ReportIncludeDryRunCheckBox.IsChecked == true,
            ReportMinDurationMinutes: reportMinDuration,
            ReportAlwaysSaveIfTradesOccurred: ReportSaveOnTradesCheckBox.IsChecked == true,
            ReportAlwaysSaveOnErrors: ReportSaveOnErrorsCheckBox.IsChecked == true,
            MaxSavedSessionReports: maxSavedReports,
            Scanner: Host.ScannerPage.CaptureScannerUiState(),
            AutomateOnLaunch: AutomateOnLaunchCheckBox.IsChecked == true,
            LastAutoTunedPreset: _lastAutoTunedPreset,
            AutoTuneEachSession: AutoTuneEachSessionCheckBox.IsChecked == true,
            AutoTuneKeeps: _autoTuneKeeps.Count == 0 ? null : _autoTuneKeeps.OrderBy(key => key, StringComparer.Ordinal).ToArray());
        Host.ReportsPage.UpdateReportRulesSummary(settings.ToSessionReportPolicy());
        return true;
    }

    internal void SaveOperatorSettingsIfValid()
    {
        if (TryReadOperatorSettings(out var opSettings))
        {
            Host.SaveOperatorSettings(opSettings);
        }
    }

    internal void SaveAllSettingsSilently()
    {
        if (TradingLimits.TryParse(MinimumProfitBox.Text, MaximumBuyGoldBox.Text, out var limits, out _))
        {
            Host.SaveLimits(limits);
        }

        if (TryReadClientSettings(out var clientSettings))
        {
            Host.SaveClientSettings(clientSettings);
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
}
