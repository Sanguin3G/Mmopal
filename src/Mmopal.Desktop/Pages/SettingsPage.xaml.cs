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

        Host.SessionPage.UpdateSessionPaceSummary(); // the pace description includes the latency waits
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

}
