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

/// <summary>Session: start and steer a trading session, watch its status, and read the activity log.</summary>
public partial class SessionPage : UserControl
{
    private readonly List<(DateTime Timestamp, string Human, string Raw)> _logEntries = new();
    private MainWindow? _host;

    public SessionPage()
    {
        InitializeComponent();
        ShowRawLogCheckBox.Checked += (_, _) => RebuildLogList();
        ShowRawLogCheckBox.Unchecked += (_, _) => RebuildLogList();
    }

    private MainWindow Host => _host ?? throw new InvalidOperationException("The page is not attached to the main window.");

    private bool Loading => _host is null || _host.IsLoadingUi;

    internal void Attach(MainWindow host) => _host = host;

    private void DockToggle_Click(object sender, RoutedEventArgs e) => Host.ToggleSideDock();

    private void OpenGuide_Click(object sender, RoutedEventArgs e) => Host.NavGuide.IsChecked = true;

    private void DismissReadinessBanner_Click(object sender, RoutedEventArgs e)
    {
        Host.ReadinessBannerDismissed = true;
        ReadinessBanner.Visibility = Visibility.Collapsed;
    }

    private void ModeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Loading) return;

        // Live trading acts on the real game, so it always reads the real game screen and arms input; any other mode
        // sends no input, so it disarms.
        var isLive = IsSelectedTag(ModeSelector, nameof(RunMode.Live));
        if (isLive && SourceSelector is not null && !IsSelectedTag(SourceSelector, "screen"))
        {
            SelectByTag(SourceSelector, "screen");
        }

        if (ArmLiveInputCheckBox is not null)
        {
            ArmLiveInputCheckBox.IsChecked = isLive;
        }

        UpdateSafeModePill();
    }

    private void SourceSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Loading || ModeSelector is null) return;

        // Replay data is a recorded sample; arming live clicks against it makes no sense, so fall back to Dry run.
        if (IsSelectedTag(SourceSelector, "replay") && IsSelectedTag(ModeSelector, nameof(RunMode.Live)))
        {
            SelectByTag(ModeSelector, nameof(RunMode.DryRun));
        }
    }

    private void ProfileSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Loading) return;
        UpdateSessionPaceSummary();
    }

    internal void UpdateSessionPaceSummary()
    {
        if (SessionPaceSummaryText is null || ProfileSelector is null)
        {
            return;
        }

        var profileTag = (ProfileSelector.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "balanced";
        var paceText = TradingPaceDescriptions.Describe(profileTag);
        var platform = Host.SettingsPage.GetSelectedLaunchPlatform();
        var acct = Host.ApiKeyVault.ActiveEntry?.DisplayLabel;
        SessionPaceSummaryText.Text = string.IsNullOrWhiteSpace(acct)
            ? $"{paceText}  •  Platform: {platform}"
            : $"{paceText}  •  Platform: {platform} ({acct})";
        // The pace description is a tooltip, not a line of text on the page.
        ProfileSelector.ToolTip = SessionPaceSummaryText.Text;
        if (PaceSafeItem is not null)
        {
            PaceSafeItem.ToolTip = TradingPaceDescriptions.Describe("safe");
            PaceBalancedItem.ToolTip = TradingPaceDescriptions.Describe("balanced");
            PaceFastItem.ToolTip = TradingPaceDescriptions.Describe("fast");
        }
    }

    private void AutoSellInventory_Changed(object sender, RoutedEventArgs e)
    {
        if (Loading) return;
        Host.SettingsPage.SaveAllSettingsSilently(); // remembered across restarts; it used to reset to off on every launch
    }

    private void ArmLiveInput_Changed(object sender, RoutedEventArgs e)
    {
        if (Loading) return;
        UpdateSafeModePill();
    }

    internal void UpdateSafeModePill()
    {
        if (ModeSelector is null || ArmLiveInputCheckBox is null ||
            SafeModePillBorder is null || SafeModePillIcon is null || SafeModePillText is null)
        {
            return;
        }

        var selectedMode = (ModeSelector.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        var isLiveMode = string.Equals(selectedMode, nameof(RunMode.Live), StringComparison.OrdinalIgnoreCase);
        var isArmed = ArmLiveInputCheckBox.IsChecked == true;

        if (isLiveMode && isArmed)
        {
            SafeModePillText.Text = "Live · Armed";
            SafeModePillBorder.ToolTip = "Live input is armed: the session sends real clicks and keystrokes to Guild Wars 2.";
            SafeModePillIcon.Text = "\uE7BA";
            var warnBrush = ResolveBrush("WarningBrush", Brushes.DarkOrange);
            SafeModePillIcon.Foreground = warnBrush;
            SafeModePillBorder.BorderBrush = warnBrush;
        }
        else if (isLiveMode)
        {
            SafeModePillText.Text = "Live · Disarmed";
            SafeModePillBorder.ToolTip = "Live mode is selected but input is not armed. Tick 'Arm live input' to let the session click.";
            SafeModePillIcon.Text = "\uE72E";
            var warnBrush = ResolveBrush("WarningBrush", Brushes.DarkOrange);
            SafeModePillIcon.Foreground = warnBrush;
            SafeModePillBorder.BorderBrush = ResolveBrush("BorderBrush", Brushes.Gray);
        }
        else
        {
            SafeModePillText.Text = "Safe · No input";
            SafeModePillBorder.ToolTip = "Dry run / Observe: no clicks or keystrokes are sent to the game.";
            SafeModePillIcon.Text = "\uE72E";
            SafeModePillIcon.Foreground = ResolveBrush("SuccessBrush", Brushes.SeaGreen);
            SafeModePillBorder.BorderBrush = ResolveBrush("BorderBrush", Brushes.Gray);
        }
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        _logEntries.Clear();
        LogList.Items.Clear();
        UpdateLogCount();
    }

    private enum LaunchStep { Waiting, Running, Done, Skipped, Failed }

    internal string UpdateStatusCard(WorkerState state, string? detail)
    {
        var (label, brushKey) = state switch
        {
            WorkerState.ProcessingTrading or WorkerState.TradingPostReady => ("Trading", "SuccessBrush"),
            WorkerState.Starting => ("Starting", "AccentBrush"),
            WorkerState.LaunchingClient or WorkerState.WaitingForLogin => ("Launching", "AccentBrush"),
            WorkerState.Fishing => ("Fishing", "SuccessBrush"),
            WorkerState.Stopping => ("Stopping", "TextMutedBrush"),
            WorkerState.Faulted => ("Faulted", "DangerBrush"),
            WorkerState.Paused => ("Paused", "WarningBrush"),
            WorkerState.Recovering => ("Recovering", "WarningBrush"),
            WorkerState.NeedsAttention => ("Needs attention", "DangerBrush"),
            WorkerState.Stopped => ("Stopped", "TextMutedBrush"),
            WorkerState.Idle => ("Idle", "TextMutedBrush"),
            _ => (state.ToString(), "TextMutedBrush")
        };
        var brush = ResolveBrush(brushKey, Brushes.Gray);
        StatusStateText.Text = label;
        StatusStateDot.Fill = brush;
        StatusCard.ToolTip = string.IsNullOrWhiteSpace(detail) ? label : $"{label}: {detail}";
        return label;
    }

    /// <summary>"72 buys and 14 sells. 7 buys are behind the top bid. 12 sells are behind the lowest ask." -> headline + detail.</summary>
    internal void UpdateOrdersCard(string? orderBook, bool buysPaused)
    {
        var text = orderBook ?? string.Empty;
        var counts = System.Text.RegularExpressions.Regex.Match(text, @"(\d+) buys? and (\d+) sells?");
        var behindBuys = System.Text.RegularExpressions.Regex.Match(text, @"(\d+) buys? (?:is|are) behind");
        var behindSells = System.Text.RegularExpressions.Regex.Match(text, @"(\d+) sells? (?:is|are) behind");
        OrdersHeadlineText.Text = counts.Success ? $"{counts.Groups[1].Value} buys · {counts.Groups[2].Value} sells" : "—";
        var detail = new List<string>();
        if (buysPaused) detail.Add("buys paused (gold floor)");
        if (behindBuys.Success) detail.Add($"{behindBuys.Groups[1].Value} outbid");
        if (behindSells.Success) detail.Add($"{behindSells.Groups[1].Value} undercut");
        OrdersDetailText.Text = detail.Count > 0 ? string.Join(" · ", detail) : counts.Success ? "all at the top of the book" : "No orders reported";
        OrdersCard.ToolTip = string.IsNullOrWhiteSpace(text) ? null : text;
    }

    internal void ShowSwapStatus(string swapText)
    {
        AfkSwapStatusText.Text = swapText;
        ScheduleHeadlineText.Text = ShortSwapHeadline(AfkSwapStatusText.Text);
        ScheduleCard.ToolTip = AfkSwapStatusText.Text;
    }

    /// <summary>"Next AFK swap (random character) in 61m at 7:33 CH." -> "Swap in 61m".</summary>
    private static string ShortSwapHeadline(string swapText)
    {
        if (swapText.Contains("disabled", StringComparison.OrdinalIgnoreCase)) return "No AFK swap";
        if (swapText.Contains("due now", StringComparison.OrdinalIgnoreCase)) return "Swap due";
        var minutes = System.Text.RegularExpressions.Regex.Match(swapText, @"in (\d+)m");
        if (!minutes.Success || !int.TryParse(minutes.Groups[1].Value, out var total)) return "AFK swap on";
        return total >= 60 ? $"Swap in {total / 60}h {total % 60:00}m" : $"Swap in {total}m";
    }

    internal void UpdateSessionClockDisplay()
    {
        if (SessionClockText is null || SessionClockIcon is null || SessionClockPillBorder is null)
        {
            return;
        }

        if (Host.SessionStartedAtUtc is not { } startedAt)
        {
            SessionClockText.Text = "00:00:00";
            SessionClockIcon.Foreground = ResolveBrush("AccentBrush", Brushes.DodgerBlue);
            SessionClockPillBorder.ToolTip = "Elapsed trade session clock (starts with each session).";
            Host.UpdateSidebarStatusTooltip();
            return;
        }

        var elapsed = DateTimeOffset.UtcNow - startedAt;
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        var hours = (int)elapsed.TotalHours;
        SessionClockText.Text = $"{hours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";

        if (Host.SessionExpiresAtUtc is { } expiresAt)
        {
            var remaining = expiresAt - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                SessionClockIcon.Foreground = ResolveBrush("WarningBrush", Brushes.DarkOrange);
                SessionCapStatusText.Text = "Session cap reached";
                SessionClockPillBorder.ToolTip = $"Elapsed: {SessionClockText.Text} • Max session cap reached (finalizing)";
            }
            else
            {
                var isFinalFiveMinutes = remaining <= TimeSpan.FromMinutes(5);
                SessionClockIcon.Foreground = isFinalFiveMinutes
                    ? ResolveBrush("WarningBrush", Brushes.DarkOrange)
                    : ResolveBrush("SuccessBrush", Brushes.SeaGreen);
                var remHours = (int)remaining.TotalHours;
                SessionCapStatusText.Text = remHours > 0
                    ? $"Stops in {remHours}h {remaining.Minutes:00}m ({expiresAt.ToLocalTime():t})"
                    : $"Stops in {Math.Ceiling(remaining.TotalMinutes):0}m ({expiresAt.ToLocalTime():t})";
                SessionClockPillBorder.ToolTip = $"Elapsed: {SessionClockText.Text} • {SessionCapStatusText.Text}";
            }
        }
        else
        {
            SessionClockIcon.Foreground = ResolveBrush("SuccessBrush", Brushes.SeaGreen);
            SessionClockPillBorder.ToolTip = $"Elapsed: {SessionClockText.Text} • No time cap";
        }

        Host.UpdateSidebarStatusTooltip();
    }

    internal void AddLog(string message, string? rawMessage = null)
    {
        var now = DateTime.Now;
        var raw = rawMessage ?? message;
        _logEntries.Add((now, message, raw));
        if (_logEntries.Count > 500)
        {
            _logEntries.RemoveAt(0);
        }

        var display = ShowRawLogCheckBox.IsChecked == true ? raw : message;
        LogList.Items.Add($"[{now:T}] {display}");
        if (LogList.Items.Count > 500)
        {
            LogList.Items.RemoveAt(0);
        }

        LogList.ScrollIntoView(LogList.Items[^1]);
        UpdateLogCount();
    }

    private void RebuildLogList()
    {
        var showRaw = ShowRawLogCheckBox.IsChecked == true;
        LogList.Items.Clear();
        foreach (var entry in _logEntries)
        {
            LogList.Items.Add($"[{entry.Timestamp:T}] {(showRaw ? entry.Raw : entry.Human)}");
        }

        if (LogList.Items.Count > 0)
        {
            LogList.ScrollIntoView(LogList.Items[^1]);
        }

        UpdateLogCount();
    }

    private void UpdateLogCount() => LogCountText.Text = $"{LogList.Items.Count} events";

    private static string ResolveTemplateRootDirectory()
    {
        const string relative = "assets/templates/tradingpost";
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 8 && current is not null; depth++, current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, relative);
            if (Directory.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return Path.GetFullPath(relative, AppContext.BaseDirectory);
    }
}
