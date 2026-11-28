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
using Mmopal.Native;
using static Mmopal.Desktop.UiHelpers;

namespace Mmopal.Desktop.Pages;

/// <summary>Loading settings into the controls and reading them back (limits, client, operator settings), and saving.</summary>
public partial class SettingsPage
{
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
        SelectComboByTag(LaunchPlatformSelector, InferLaunchPlatform(settings));

        Gw2ExecutablePathBox.Text = string.IsNullOrWhiteSpace(settings.ExecutablePath)
            ? detectedPath ?? string.Empty
            : settings.ExecutablePath;
        Gw2LaunchArgsBox.Text = string.IsNullOrWhiteSpace(settings.LaunchArguments) ||
                                string.Equals(settings.LaunchArguments.Trim(), "-windowed", StringComparison.OrdinalIgnoreCase)
            ? "-autologin -windowed"
            : settings.LaunchArguments;
        Gw2WidthBox.Text = settings.WindowWidth > 0 ? settings.WindowWidth.ToString(CultureInfo.InvariantCulture) : "1024";
        Gw2HeightBox.Text = settings.WindowHeight > 0 ? settings.WindowHeight.ToString(CultureInfo.InvariantCulture) : "768";
        MaxLaunchAttemptsBox.Text = ClampedText(settings.MaxLaunchAttempts, 1, 5);
        WindowWaitSecondsBox.Text = ClampedText(settings.WindowWaitTimeoutSeconds, 5, 180);
        MaxRecoveryRetriesBox.Text = ClampedText(settings.MaxRecoveryRetries, 0, 10);
        MaxGameRestartsBox.Text = ClampedText(settings.MaxGameRestartsPerSession, 0, 5);
        AutoRestartOnFailureCheckBox.IsChecked = settings.AutoRestartGameOnFailure;
        MaxSessionHoursBox.Text = settings.FormatMaxSessionHours();
        AutoCloseGameOnCapCheckBox.IsChecked = settings.AutoCloseGameOnSessionCap;
        AutoCloseAppOnCapCheckBox.IsChecked = settings.AutoCloseAppOnSessionCap;
        ShutdownPcOnCapCheckBox.IsChecked = settings.ShutdownPcOnSessionCap;
        PcShutdownCountdownBox.Text = ClampedText(settings.PcShutdownCountdownSeconds, 10, 600);
        ParkedCharactersBox.Text = Math.Max(1, settings.ParkedCharacterCount).ToString(CultureInfo.InvariantCulture);
        InteractKeyBox.Text = string.IsNullOrWhiteSpace(settings.InteractKeybind) ? "F" : settings.InteractKeybind;
        TpKeyBox.Text = string.IsNullOrWhiteSpace(settings.TradingPostKeybind) ? "O" : settings.TradingPostKeybind;
        RelistStaleSellsCheckBox.IsChecked = settings.RelistStaleUndercutSells;
        MaxBuyOrderShareBox.Text = ClampedText(settings.MaxBuyOrderSharePercent, 0, 100);
        SwapCharacterHoursBox.Text = settings.FormatSwapHours();
        SwapJitterMinutesBox.Text = settings.SwapCharacterJitterMinutes.ToString(CultureInfo.InvariantCulture);
        SnapWindowOnStartCheckBox.IsChecked = settings.AutoPrepareOnScreenStart;
        AutoLaunchGw2CheckBox.IsChecked = settings.AutoLaunchIfNotRunning;
        RandomCharacterSwapCheckBox.IsChecked = settings.RandomizeCharacterSelection;

        var latencyId = string.IsNullOrWhiteSpace(settings.LatencyProfile) ? "HighPing" : settings.LatencyProfile;
        SelectComboByTag(LatencyProfileSelector, latencyId);
        SelectComboByTag(PriceEntryModeSelector, settings.TypesExactPrice ? Gw2ClientSettings.PriceEntryTypeExact : Gw2ClientSettings.PriceEntryClickRow);
        UiLoadWaitMsBox.Text = ClampedText(settings.UiLoadWaitMs, 500, 30_000);
        ActionSettleDelayMsBox.Text = ClampedText(settings.ActionSettleDelayMs, 100, 5_000);
        ApiTimeoutSecondsBox.Text = ClampedText(settings.ApiTimeoutSeconds, 15, 120);
        if (Gw2LatencyPresets.FindById(latencyId) is { } latencyPreset)
        {
            LatencyDescriptionText.Text = latencyPreset.Description;
        }

        var dpi = Host.DetectDisplayScale();
        Gw2DetectHealthText.Text = $"Client: {DescribeInstall(installs.FirstOrDefault())}  •  Display Scale: {dpi.Summary}";
        ShowSessionCapStatus(settings);
    }

    private static string InferLaunchPlatform(Gw2ClientSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.LaunchPlatform))
        {
            return settings.LaunchPlatform;
        }

        return settings.IsSteamPlatform ? Gw2ClientSettings.PlatformSteam : Gw2ClientSettings.PlatformArenaNet;
    }

    private string DescribeInstall(Gw2InstallCandidate? install)
    {
        if (install is not null)
        {
            return $"{install.Kind} ({install.Source})";
        }

        return string.IsNullOrWhiteSpace(Gw2ExecutablePathBox.Text) ? "Not detected yet" : "Configured path";
    }

    private void ShowSessionCapStatus(Gw2ClientSettings settings) =>
        Host.SessionPage.SessionCapStatusText.Text = settings.MaxSessionDurationMinutes is > 0
            ? $"Session cap: {settings.FormatMaxSessionHours()}h"
            : "Session cap: disabled";

    private static string ClampedText(int value, int min, int max) =>
        Math.Clamp(value, min, max).ToString(CultureInfo.InvariantCulture);

    /// <summary>Reads a whole number, clamped to its allowed range; blank or unreadable text gives the default.</summary>
    private static int ReadClampedInt(TextBox box, int min, int max, int fallback) =>
        int.TryParse(box.Text.Trim(), out var value) ? Math.Clamp(value, min, max) : fallback;

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
        Host.SessionPage.ScheduledStartCheckBox.IsChecked = settings.ScheduledStart is not null;
        if (settings.ScheduledStart is { } scheduled) Host.SessionPage.ScheduledStartBox.Text = scheduled;
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
        ReportMinDurationBox.Text = ClampedText(settings.ReportMinDurationMinutes, 0, 1440);
        MaxSavedReportsBox.Text = ClampedText(settings.MaxSavedSessionReports, 10, 1000);
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
        var latencyProfile = (LatencyProfileSelector.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "HighPing";

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
            MaxLaunchAttempts: ReadClampedInt(MaxLaunchAttemptsBox, 1, 5, fallback: 2),
            WindowWaitTimeoutSeconds: ReadClampedInt(WindowWaitSecondsBox, 5, 180, fallback: 45),
            MaxSessionDurationMinutes: maxSessionMinutes,
            ParkedCharacterCount: ReadClampedInt(ParkedCharactersBox, 1, 64, fallback: 4),
            InteractKeybind: NullIfBlank(InteractKeyBox.Text) ?? "F",
            TradingPostKeybind: NullIfBlank(TpKeyBox.Text) ?? "O",
            RelistStaleUndercutSells: RelistStaleSellsCheckBox.IsChecked == true,
            MaxBuyOrderSharePercent: ReadClampedInt(MaxBuyOrderShareBox, 0, 100, fallback: 50),
            LatencyProfile: latencyProfile,
            UiLoadWaitMs: ReadClampedInt(UiLoadWaitMsBox, 500, 30_000, fallback: 7500),
            ActionSettleDelayMs: ReadClampedInt(ActionSettleDelayMsBox, 100, 5_000, fallback: 900),
            ApiTimeoutSeconds: ReadClampedInt(ApiTimeoutSecondsBox, 15, 120, fallback: 60),
            LaunchPlatform: GetSelectedLaunchPlatform(),
            MaxRecoveryRetries: ReadClampedInt(MaxRecoveryRetriesBox, 0, 10, fallback: 3),
            AutoRestartGameOnFailure: AutoRestartOnFailureCheckBox.IsChecked == true,
            MaxGameRestartsPerSession: ReadClampedInt(MaxGameRestartsBox, 0, 5, fallback: 2),
            AutoCloseGameOnSessionCap: AutoCloseGameOnCapCheckBox.IsChecked == true,
            AutoCloseAppOnSessionCap: AutoCloseAppOnCapCheckBox.IsChecked == true,
            ShutdownPcOnSessionCap: ShutdownPcOnCapCheckBox.IsChecked == true,
            PcShutdownCountdownSeconds: ReadClampedInt(PcShutdownCountdownBox, 10, 600, fallback: 60),
            PriceEntryMode: (PriceEntryModeSelector.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? Gw2ClientSettings.PriceEntryTypeExact);
        ShowSessionCapStatus(settings);
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
        var reportMinDuration = ReadClampedInt(ReportMinDurationBox, 0, 1440, fallback: 5);
        var maxSavedReports = ReadClampedInt(MaxSavedReportsBox, 10, 1000, fallback: 100);

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
            AutoTuneKeeps: _autoTuneKeeps.Count == 0 ? null : _autoTuneKeeps.OrderBy(key => key, StringComparer.Ordinal).ToArray(),
            ScheduledStart: Host.SessionPage.ScheduledStartTime?.ToString("HH:mm", CultureInfo.InvariantCulture));
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
}
