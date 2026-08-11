using System.IO;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Mmopal.Contracts;

namespace Mmopal.Desktop;

public sealed class DesktopWorkerSupervisor : IAsyncDisposable
{
    private readonly string _pipeName = $"Mmopal.Worker.{Environment.ProcessId}";
    private Process? _process;
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;

    public string PipeName => _pipeName;

    public async Task EnsureStartedAsync(CancellationToken cancellationToken = default)
    {
        if (_pipe?.IsConnected == true) return;

        var workerPath = Path.Combine(AppContext.BaseDirectory, "Mmopal.Worker.exe");
        if (!File.Exists(workerPath))
        {
            throw new FileNotFoundException("The worker executable was not found beside the desktop application.", workerPath);
        }

        _process = Process.Start(new ProcessStartInfo
        {
            FileName = workerPath,
            Arguments = $"--pipe \"{_pipeName}\"",
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        });

        _pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await _pipe.ConnectAsync(5000, cancellationToken);
        _reader = new StreamReader(_pipe, Encoding.UTF8, leaveOpen: true);
        _writer = new StreamWriter(_pipe, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };
        _ = await ReadEventAsync(cancellationToken);
    }

    public async Task<WorkerEvent> SendAsync(WorkerCommand command, CancellationToken cancellationToken = default)
    {
        await EnsureStartedAsync(cancellationToken);
        var request = command with { RequestId = command.RequestId ?? Guid.NewGuid().ToString("N") };
        await _writer!.WriteLineAsync(JsonSerializer.Serialize(request));

        while (true)
        {
            var response = await ReadEventAsync(cancellationToken);
            if (response is null)
            {
                return new WorkerEvent(WorkerEventTypes.Error, "Worker disconnected.", request.RequestId, DateTimeOffset.UtcNow);
            }

            if (response.CorrelationId == request.RequestId)
            {
                return response;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_pipe?.IsConnected == true)
        {
            try
            {
                await SendAsync(new WorkerCommand(WorkerCommandTypes.Shutdown), CancellationToken.None);
            }
            catch
            {
                // The worker may already have exited; disposal remains best-effort.
            }
        }

        _reader?.Dispose();
        _writer?.Dispose();
        _pipe?.Dispose();
        if (_process is { HasExited: false })
        {
            _process.Kill(entireProcessTree: true);
        }

        _process?.Dispose();
    }

    private async Task<WorkerEvent?> ReadEventAsync(CancellationToken cancellationToken)
    {
        var line = await _reader!.ReadLineAsync(cancellationToken);
        return line is null ? null : JsonSerializer.Deserialize<WorkerEvent>(line);
    }
}
