using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Mmopal.Gw2;
using Mmopal.Native;
using static Mmopal.Desktop.UiHelpers;

namespace Mmopal.Desktop.Pages;

/// <summary>Guide &amp; Setup: the readiness checklist and the local market schedule.</summary>
public partial class GuidePage : UserControl
{
    private MainWindow? _host;

    public GuidePage()
    {
        InitializeComponent();
    }

    private MainWindow Host => _host ?? throw new InvalidOperationException("The page is not attached to the main window.");

    internal void Attach(MainWindow host) => _host = host;

    private void GoToSettings_Click(object sender, RoutedEventArgs e) => Host.NavSettings.IsChecked = true;

    private void GoToScanner_Click(object sender, RoutedEventArgs e) => Host.NavScanner.IsChecked = true;

    private void RefreshChecklist_Click(object sender, RoutedEventArgs e) => Host.RefreshReadinessChecklist();

    private async void DetectGw2_Click(object sender, RoutedEventArgs e) => await Host.DetectGw2Async();

    /// <summary>Updates the four checks and returns how many pass.</summary>
    internal int RefreshChecklist()
    {
        var passBrush = ResolveBrush("SuccessBrush", Brushes.SeaGreen);
        var warnBrush = ResolveBrush("WarningBrush", Brushes.DarkOrange);
        var readyCount = 0;

        var hasKey = Host.HasVerifiedApiKey || !string.IsNullOrWhiteSpace(Host.ResolveConfiguredApiKey());
        if (hasKey)
        {
            readyCount++;
            CheckApiKeyIcon.Text = "";
            CheckApiKeyIcon.Foreground = passBrush;
            CheckApiKeyText.Text = Host.HasVerifiedApiKey
                ? "GW2 API Key verified and stored securely via Windows DPAPI."
                : "GW2 API Key configured (click Test Key in Settings to verify scopes).";
        }
        else
        {
            CheckApiKeyIcon.Text = "";
            CheckApiKeyIcon.Foreground = warnBrush;
            CheckApiKeyText.Text = "GW2 API Key missing — add a key with account, tradingpost, wallet scopes in Settings.";
        }

        var isSteamMode = string.Equals(Host.GetSelectedLaunchPlatform(), Gw2ClientSettings.PlatformSteam, StringComparison.OrdinalIgnoreCase);
        var gw2Path = Host.Gw2ExecutablePathBox.Text?.Trim();
        var hasGw2 = isSteamMode ||
                     (!string.IsNullOrWhiteSpace(gw2Path) &&
                      (Win32ProcessController.IsUriLaunch(gw2Path) || File.Exists(gw2Path)));
        if (hasGw2)
        {
            readyCount++;
            CheckGw2Icon.Text = "";
            CheckGw2Icon.Foreground = passBrush;
            CheckGw2Text.Text = isSteamMode
                ? $"GW2 client configured: Steam Auto-Login ({Gw2ClientSettings.SteamLaunchUri})"
                : $"GW2 client configured: {gw2Path}";
        }
        else
        {
            CheckGw2Icon.Text = "";
            CheckGw2Icon.Foreground = warnBrush;
            CheckGw2Text.Text = string.IsNullOrWhiteSpace(gw2Path)
                ? "GW2 client path not set — click Auto-Detect GW2 or Browse in Settings."
                : $"Configured GW2 path not found on disk: {gw2Path}";
        }

        var dpi = Host.DetectDisplayScale();
        if (dpi.IsRecommendedScale)
        {
            readyCount++;
            CheckDpiIcon.Text = "";
            CheckDpiIcon.Foreground = passBrush;
        }
        else
        {
            CheckDpiIcon.Text = "";
            CheckDpiIcon.Foreground = warnBrush;
        }

        CheckDpiText.Text = $"Windows Display Scaling: {dpi.Summary}";

        var customItemCount = Host.CustomShoppingList.Count;
        var candidatesPath = Host.CandidatesPathBox.Text;
        var hasCandidates = customItemCount > 0 || !string.IsNullOrWhiteSpace(candidatesPath);
        if (hasCandidates)
        {
            readyCount++;
            CheckCandidatesIcon.Text = "";
            CheckCandidatesIcon.Foreground = passBrush;
            CheckCandidatesText.Text = customItemCount > 0
                ? $"Flip targets ready ({customItemCount} custom items + optional session file)."
                : $"Flip targets ready ({Path.GetFileName(candidatesPath.Trim())}).";
        }
        else
        {
            CheckCandidatesIcon.Text = "";
            CheckCandidatesIcon.Foreground = warnBrush;
            CheckCandidatesText.Text = "No flip targets loaded yet — run Market Scanner and click 'Use All for Session'.";
        }

        return readyCount;
    }

    /// <summary>Shows today's market windows in local time and highlights the current one.</summary>
    internal void UpdateSchedule(Gw2MarketCycleInfo info, DateTimeOffset nowUtc)
    {
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

    private static void SetScheduleTileHighlight(Border tile, bool isActive)
    {
        tile.Background = isActive
            ? ResolveBrush("AccentSoftBrush", ResolveBrush("SurfaceRaisedBrush", Brushes.DimGray))
            : ResolveBrush("SurfaceRaisedBrush", Brushes.DimGray);
        tile.BorderBrush = isActive
            ? ResolveBrush("AccentBrush", Brushes.DodgerBlue)
            : ResolveBrush("BorderBrush", Brushes.Gray);
        tile.BorderThickness = new Thickness(isActive ? 2 : 1);
    }
}
