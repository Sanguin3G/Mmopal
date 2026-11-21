using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Mmopal.Contracts;
using Mmopal.Infrastructure;
using Mmopal.Native;

namespace Mmopal.Desktop;

public sealed class DesktopWorkerSupervisor : IDesktopWorkerSupervisor
{
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<WorkerEvent>> _pending = new(StringComparer.Ordinal);
    private readonly ChildProcessJob _job = new();
    private readonly Func<string?> _resolveApiKey;
    private string _pipeName = CreatePipeName();
    private Process? _process;
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private CancellationTokenSource? _eventPumpCancellation;
    private Task? _eventPump;

    public DesktopWorkerSupervisor(Func<string?>? resolveApiKey = null)
    {
        _resolveApiKey = resolveApiKey ?? LoadDefaultApiKey;
    }

    public string PipeName => _pipeName;

    public event EventHandler<WorkerEvent>? EventReceived;

    public async Task RestartWorkerAsync(CancellationToken cancellationToken = default)
    {
        await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopPreviousInstanceAsync().ConfigureAwait(false);
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task EnsureStartedAsync(CancellationToken cancellationToken = default)
    {
        if (IsConnectedAndRunning()) return;

        await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsConnectedAndRunning()) return;

            await StopPreviousInstanceAsync().ConfigureAwait(false);

            var workerPath = Path.Combine(AppContext.BaseDirectory, "Mmopal.Worker.exe");
            if (!File.Exists(workerPath))
            {
                throw new FileNotFoundException("The worker executable was not found beside the desktop application.", workerPath);
            }

            _pipeName = CreatePipeName();
            var startInfo = new ProcessStartInfo
            {
                FileName = workerPath,
                Arguments = $"--pipe \"{_pipeName}\" --parent-pid {Environment.ProcessId}",
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            var apiKey = _resolveApiKey();
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                startInfo.Environment["MMOPAL_Native__Gw2ApiKey"] = apiKey;
            }

            _process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("The worker process could not be started.");
            _job.TryAssign(_process);

            _pipe = new NamedPipeClientStream(
                ".",
                _pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await _pipe.ConnectAsync(5000, cancellationToken).ConfigureAwait(false);
            _reader = new StreamReader(_pipe, Encoding.UTF8, leaveOpen: true);
            _writer = new StreamWriter(_pipe, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };

            var initialEvent = await ReadEventAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The worker disconnected before reporting its initial status.");
            EventReceived?.Invoke(this, initialEvent);

            _eventPumpCancellation = new CancellationTokenSource();
            _eventPump = ReadEventsAsync(_eventPumpCancellation.Token);
        }
        catch
        {
            await StopPreviousInstanceAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    private static string? LoadDefaultApiKey()
    {
        var profiles = RuntimePaths.Create().Profiles;
        var vaultPath = Path.Combine(profiles, "gw2-api-keys.dpapi");
        var legacyPath = Path.Combine(profiles, "gw2-api-key.dpapi");
        return ApiKeyVaultStore.LoadActiveApiKey(vaultPath, legacyPath);
    }

    public async Task<WorkerEvent> SendAsync(WorkerCommand command, CancellationToken cancellationToken = default)
    {
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        var request = command with { RequestId = command.RequestId ?? Guid.NewGuid().ToString("N") };
        var response = new TaskCompletionSource<WorkerEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(request.RequestId!, response))
        {
            throw new InvalidOperationException("A worker command used a duplicate request id.");
        }

        try
        {
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _writer!.WriteLineAsync(JsonSerializer.Serialize(request)).ConfigureAwait(false);
            }
            finally
            {
                _writeGate.Release();
            }

            return await response.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(request.RequestId!, out _);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (IsConnectedAndRunning())
        {
            try
            {
                using var shutdownTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
                await SendAsync(new WorkerCommand(WorkerCommandTypes.Shutdown), shutdownTimeout.Token).ConfigureAwait(false);
            }
            catch
            {
                // The worker may already have exited; disposal remains best-effort.
            }
        }

        FailPending("The desktop application is closing.");
        await StopPreviousInstanceAsync().ConfigureAwait(false);
        _job.Dispose();
        _connectionGate.Dispose();
        _writeGate.Dispose();
    }

    private bool IsConnectedAndRunning() =>
        _pipe?.IsConnected == true && _process is { HasExited: false };

    private static string CreatePipeName() =>
        $"Mmopal.Worker.{Environment.ProcessId}.{Guid.NewGuid():N}";

    private async Task StopPreviousInstanceAsync()
    {
        _eventPumpCancellation?.Cancel();
        if (_eventPump is not null)
        {
            try
            {
                await _eventPump.ConfigureAwait(false);
            }
            catch
            {
                // Cancellation is expected while tearing down a previous worker session; anything else is
                // best-effort cleanup before restart or shutdown.
            }
        }

        _eventPumpCancellation?.Dispose();
        _eventPumpCancellation = null;
        _eventPump = null;

        DisposeTransport();
        StopProcess();
    }

    private async Task ReadEventsAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var workerEvent = await ReadEventAsync(cancellationToken).ConfigureAwait(false);
                if (workerEvent is null)
                {
                    FailPending("Worker disconnected.");
                    return;
                }

                if (workerEvent.CorrelationId is { } correlationId &&
                    _pending.TryGetValue(correlationId, out var pendingResponse))
                {
                    pendingResponse.TrySetResult(workerEvent);
                    continue;
                }

                EventReceived?.Invoke(this, workerEvent);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected while the desktop shuts down.
        }
        catch (Exception exception)
        {
            FailPending($"Worker event stream failed: {exception.Message}");
            EventReceived?.Invoke(this, new WorkerEvent(
                WorkerEventTypes.Error,
                exception.Message,
                null,
                DateTimeOffset.UtcNow));
        }
    }

    private async Task<WorkerEvent?> ReadEventAsync(CancellationToken cancellationToken)
    {
        var line = await _reader!.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        return line is null ? null : JsonSerializer.Deserialize<WorkerEvent>(line);
    }

    private void FailPending(string error)
    {
        foreach (var pending in _pending.Values)
        {
            pending.TrySetResult(new WorkerEvent(WorkerEventTypes.Error, error, null, DateTimeOffset.UtcNow));
        }
    }

    private void DisposeTransport()
    {
        _reader?.Dispose();
        _writer?.Dispose();
        _pipe?.Dispose();
        _reader = null;
        _writer = null;
        _pipe = null;
    }

    private void StopProcess()
    {
        if (_process is { HasExited: false })
        {
            try
            {
                if (!_process.WaitForExit(500))
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Process may have exited concurrently.
            }
        }

        _process?.Dispose();
        _process = null;
    }
}
