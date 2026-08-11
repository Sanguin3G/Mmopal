using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Mmopal.Contracts;

namespace Mmopal.Worker;

public sealed class NamedPipeWorkerServer
{
    private readonly string _pipeName;
    private readonly WorkerRuntime _runtime;
    private readonly ILogger _logger;

    public NamedPipeWorkerServer(string pipeName, WorkerRuntime runtime, ILogger logger)
    {
        _pipeName = pipeName;
        _runtime = runtime;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await using var pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                _logger.LogInformation("Waiting for desktop connection on pipe {PipeName}.", _pipeName);
                await pipe.WaitForConnectionAsync(cancellationToken);
                using var connectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var events = Channel.CreateUnbounded<WorkerEvent>(new UnboundedChannelOptions
                {
                    SingleReader = true,
                    SingleWriter = false
                });
                EventHandler<WorkerEvent> eventHandler = (_, workerEvent) => events.Writer.TryWrite(workerEvent);
                _runtime.EventRaised += eventHandler;
                events.Writer.TryWrite(new WorkerEvent(WorkerEventTypes.Status, JsonSerializer.Serialize(_runtime.Status), Timestamp: DateTimeOffset.UtcNow));

                var sender = Task.Run(async () =>
                {
                    await foreach (var workerEvent in events.Reader.ReadAllAsync(connectionCancellation.Token))
                    {
                        await SendAsync(pipe, workerEvent, connectionCancellation.Token);
                    }
                }, connectionCancellation.Token);

                using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                var shutdownRequested = false;
                while (pipe.IsConnected && !connectionCancellation.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(connectionCancellation.Token);
                    if (line is null) break;

                    WorkerCommand? command;
                    try
                    {
                        command = JsonSerializer.Deserialize<WorkerCommand>(line);
                    }
                    catch (JsonException exception)
                    {
                        events.Writer.TryWrite(new WorkerEvent(WorkerEventTypes.Error, $"Invalid command: {exception.Message}", Timestamp: DateTimeOffset.UtcNow));
                        continue;
                    }

                    if (command is null) continue;
                    var result = await _runtime.HandleAsync(command, connectionCancellation.Token);
                    events.Writer.TryWrite(result with
                    {
                        CorrelationId = command.RequestId ?? Guid.NewGuid().ToString("N"),
                        Timestamp = result.Timestamp ?? DateTimeOffset.UtcNow
                    });
                    if (command.Type.Equals(WorkerCommandTypes.Shutdown, StringComparison.OrdinalIgnoreCase))
                    {
                        shutdownRequested = true;
                        break;
                    }
                }

                if (!shutdownRequested)
                {
                    connectionCancellation.Cancel();
                }

                events.Writer.TryComplete();
                try { await sender; } catch (OperationCanceledException) { }
                _runtime.EventRaised -= eventHandler;
                if (shutdownRequested) break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Named pipe server cancelled.");
        }
        finally
        {
        }
    }

    private static async Task SendAsync(Stream stream, WorkerEvent workerEvent, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(workerEvent);
        var bytes = Encoding.UTF8.GetBytes(json + Environment.NewLine);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
}
