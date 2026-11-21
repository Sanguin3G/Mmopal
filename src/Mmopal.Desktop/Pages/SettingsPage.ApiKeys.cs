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

/// <summary>Accounts & API tab: the API key vault table (use, order, copy, delete, names that save themselves), add, verify and test.</summary>
public partial class SettingsPage
{
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
