using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

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
        Action onCloseAppOnly)
    {
        _isPcShutdown = isPcShutdown;
        _onExecutePrimary = onExecutePrimary ?? throw new ArgumentNullException(nameof(onExecutePrimary));
        _onCloseAppOnly = onCloseAppOnly ?? throw new ArgumentNullException(nameof(onCloseAppOnly));
        _totalSeconds = Math.Clamp(countdownSeconds, 5, 600);
        _remainingSeconds = _totalSeconds;

        InitializeComponent();

        if (isPcShutdown)
        {
            Title = "Mmopal — Scheduled PC Shutdown";
            HeadlineText.Text = "Session Cap Reached — PC Shutdown";
            SubheadlineText.Text = summaryNote;
            BodyMessageText.Text =
                "Guild Wars 2 has been closed cleanly and your trade report was finalized. " +
                "This computer will shut down automatically when the countdown hits 0 (even if unattended).";
            ModeIconText.Text = "\uE7E8";
            CancelButton.Content = "Cancel Shutdown";
            CloseAppOnlyButton.Visibility = Visibility.Visible;
            ExecuteNowButton.Content = "Shut Down Now";
        }
        else
        {
            Title = "Mmopal — Auto-Closing App";
            HeadlineText.Text = "Session Cap Reached";
            SubheadlineText.Text = summaryNote;
            BodyMessageText.Text =
                "Guild Wars 2 has been closed cleanly and your trade report was finalized. " +
                "Mmopal will close automatically when the countdown hits 0.";
            ModeIconText.Text = "\uE916";
            if (Application.Current?.TryFindResource("AccentBrush") is Brush accent)
            {
                ModeIconText.Foreground = accent;
                CountdownProgressBar.Foreground = accent;
            }

            CancelButton.Content = "Keep Mmopal Open";
            CloseAppOnlyButton.Visibility = Visibility.Collapsed;
            ExecuteNowButton.Content = "Close Now";
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
        CountdownSecondsText.Text = $"{_remainingSeconds}s";
        CountdownProgressBar.Value = _remainingSeconds;
    }

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
