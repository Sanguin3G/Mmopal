using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Mmopal.Contracts;

namespace Mmopal.Desktop;

public partial class SessionCompletionWindow : Window
{
    private readonly bool _isPcShutdown;
    private readonly Action _onCloseAppOnly;
    private readonly Action _onExecutePrimary;
    private readonly DispatcherTimer _timer;
    private readonly int _totalSeconds;
    private int _remainingSeconds;
    private bool _actionHandled;

    public SessionCompletionWindow(
        bool isPcShutdown,
        int countdownSeconds,
        string summaryNote,
        Action onExecutePrimary,
        Action onCloseAppOnly,
        TradingSessionReport? report = null)
    {
        _isPcShutdown = isPcShutdown;
        _onExecutePrimary = onExecutePrimary ?? throw new ArgumentNullException(nameof(onExecutePrimary));
        _onCloseAppOnly = onCloseAppOnly ?? throw new ArgumentNullException(nameof(onCloseAppOnly));
        _totalSeconds = Math.Clamp(countdownSeconds, 5, 600);
        _remainingSeconds = _totalSeconds;

        InitializeComponent();

        HeadlineText.Text = "Session complete";
        SubheadlineText.Text = summaryNote;
        ShowStats(report);
        if (isPcShutdown)
        {
            Title = "Mmopal — PC shutdown";
            BodyMessageText.Text = "Guild Wars 2 was logged out and closed. This PC shuts down when the countdown ends, even if nobody is here.";
            CountdownLabelText.Text = "This PC shuts down in";
            ModeIconText.Text = "\uE7E8";
            if (Application.Current?.TryFindResource("WarningBrush") is Brush warning)
            {
                ModeIconText.Foreground = warning;
                CountdownProgressBar.Foreground = warning;
            }

            if (Application.Current?.TryFindResource("DangerButton") is Style danger)
            {
                ExecuteNowButton.Style = danger;
            }

            CancelButton.Content = "Cancel shutdown";
            CloseAppOnlyButton.Visibility = Visibility.Visible;
            ExecuteNowButton.Content = "Shut down now";
        }
        else
        {
            Title = "Mmopal — Session complete";
            BodyMessageText.Text = "Guild Wars 2 was logged out and closed.";
            CountdownLabelText.Text = "Mmopal closes in";
            CancelButton.Content = "Keep Mmopal open";
            CloseAppOnlyButton.Visibility = Visibility.Collapsed;
            ExecuteNowButton.Content = "Close now";
        }

        CountdownProgressBar.Maximum = _totalSeconds;
        UpdateCountdownDisplay();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += Timer_Tick;
        SourceInitialized += (_, _) =>
        {
            if (Application.Current is App app)
            {
                app.ApplyWindowTitleBar(this);
            }
        };
        Loaded += (_, _) => _timer.Start();
        Closing += Window_Closing;
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        if (_actionHandled)
        {
            return;
        }

        _remainingSeconds = Math.Max(0, _remainingSeconds - 1);
        UpdateCountdownDisplay();
        if (_remainingSeconds <= 0)
        {
            TriggerPrimaryAction();
        }
    }

    private void UpdateCountdownDisplay()
    {
        CountdownSecondsText.Text = $"{_remainingSeconds} s";
        CountdownProgressBar.Value = _remainingSeconds;
    }

    /// <summary>Run time, orders, listings and coins collected; the strip hides when there is no report.</summary>
    private void ShowStats(TradingSessionReport? report)
    {
        if (report is null)
        {
            StatsBorder.Visibility = Visibility.Collapsed;
            return;
        }

        var duration = TimeSpan.FromSeconds(Math.Max(0, report.DurationSeconds));
        StatDurationText.Text = duration.TotalHours >= 1 ? $"{(int)duration.TotalHours}h {duration.Minutes:00}m" : $"{duration.Minutes}m";
        StatBuysText.Text = report.BuyOrdersPlaced.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);
        StatListingsText.Text = report.SellOrdersListed.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);
        StatCoinsText.Text = CompactCoins(report.DeliveryCoinsCollectedCopper);
        StatCoinsText.ToolTip = Mmopal.Gw2.MarketScanItem.FormatCoins(report.DeliveryCoinsCollectedCopper);
    }

    /// <summary>Largest coin only ("62g", "15s", "40c"): the exact amount is in the tooltip.</summary>
    internal static string CompactCoins(long copper) =>
        copper >= 10_000 ? $"{copper / 10_000}g" : copper >= 100 ? $"{copper / 100}s" : $"{Math.Max(0, copper)}c";

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _actionHandled = true;
        _timer.Stop();
        Close();
    }

    private void CloseAppOnly_Click(object sender, RoutedEventArgs e)
    {
        _actionHandled = true;
        _timer.Stop();
        Close();
        _onCloseAppOnly();
    }

    private void ExecuteNow_Click(object sender, RoutedEventArgs e) => TriggerPrimaryAction();

    private void TriggerPrimaryAction()
    {
        if (_actionHandled)
        {
            return;
        }

        _actionHandled = true;
        _timer.Stop();
        Close();
        _onExecutePrimary();
    }

    private void Window_Closing(object? sender, CancelEventArgs e) => _timer.Stop();
}
