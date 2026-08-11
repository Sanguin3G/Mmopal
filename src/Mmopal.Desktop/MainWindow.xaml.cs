using System.Windows;
using Mmopal.Contracts;

namespace Mmopal.Desktop;

public partial class MainWindow : Window
{
    private readonly DesktopWorkerSupervisor _supervisor = new();

    public MainWindow()
    {
        InitializeComponent();
        Closed += async (_, _) => await _supervisor.DisposeAsync();
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        var bot = Enum.Parse<BotKind>(((System.Windows.Controls.ComboBoxItem)BotSelector.SelectedItem).Tag.ToString()!);
        var mode = Enum.Parse<RunMode>(((System.Windows.Controls.ComboBoxItem)ModeSelector.SelectedItem).Tag.ToString()!);
        var profile = ((System.Windows.Controls.ComboBoxItem)ProfileSelector.SelectedItem).Tag.ToString()!;
        await SendAsync(new WorkerCommand(WorkerCommandTypes.Start, Guid.NewGuid().ToString("N"), bot, mode, profile));
    }

    private async void Pause_Click(object sender, RoutedEventArgs e) => await SendAsync(new WorkerCommand(WorkerCommandTypes.Pause));

    private async void Resume_Click(object sender, RoutedEventArgs e) => await SendAsync(new WorkerCommand(WorkerCommandTypes.Resume));

    private async void Stop_Click(object sender, RoutedEventArgs e) => await SendAsync(new WorkerCommand(WorkerCommandTypes.Stop));

    private async void EmergencyStop_Click(object sender, RoutedEventArgs e) => await SendAsync(new WorkerCommand(WorkerCommandTypes.EmergencyStop));

    private async Task SendAsync(WorkerCommand command)
    {
        try
        {
            var response = await _supervisor.SendAsync(command);
            LogList.Items.Add($"[{DateTime.Now:T}] {response.Type}: {response.Payload}");
            if (response.Type == WorkerEventTypes.Status)
            {
                var status = System.Text.Json.JsonSerializer.Deserialize<WorkerStatus>(response.Payload);
                StatusText.Text = status is null ? response.Payload : $"{status.State} — {status.Message ?? status.LastAction}";
            }
        }
        catch (Exception exception)
        {
            LogList.Items.Add($"[{DateTime.Now:T}] ERROR: {exception.Message}");
            StatusText.Text = "Worker unavailable";
        }
    }
}
