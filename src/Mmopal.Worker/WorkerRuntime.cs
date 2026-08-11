using System.Text.Json;
using Microsoft.Extensions.Logging;
using Mmopal.Contracts;
using Mmopal.Infrastructure;

namespace Mmopal.Worker;

public sealed class WorkerRuntime
{
    private readonly CheckpointStore _store;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _runCancellation;
    private Task? _runTask;
    private string? _runId;
    private WorkerStatus _status = new(WorkerState.Idle, null, null, null, null, DateTimeOffset.UtcNow);

    public WorkerRuntime(CheckpointStore store, ILogger logger)
    {
        _store = store;
        _logger = logger;
    }

    public event EventHandler<WorkerEvent>? EventRaised;

    public WorkerStatus Status => _status;

    public async Task<WorkerEvent> HandleAsync(WorkerCommand command, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return command.Type.ToLowerInvariant() switch
            {
                WorkerCommandTypes.Start => await StartAsync(command, cancellationToken),
                WorkerCommandTypes.Pause => Pause(),
                WorkerCommandTypes.Resume => Resume(),
                WorkerCommandTypes.Stop => await StopCommandAsync(cancellationToken),
                WorkerCommandTypes.EmergencyStop => await EmergencyStopAsync(cancellationToken),
                WorkerCommandTypes.Status => StatusEvent(),
                WorkerCommandTypes.Shutdown => await StopCommandAsync(cancellationToken),
                _ => ErrorEvent($"Unknown worker command '{command.Type}'.")
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _runCancellation?.Cancel();
        if (_runTask is not null)
        {
            try { await _runTask.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { }
        }

        _runCancellation?.Dispose();
        _runCancellation = null;
        _runTask = null;
        if (_runId is not null)
        {
            await _store.CompleteRunAsync(_runId, WorkerState.Stopped.ToString(), cancellationToken);
        }
        _runId = null;
        SetStatus(WorkerState.Stopped, message: "Worker stopped.");
    }

    private async Task<WorkerEvent> StartAsync(WorkerCommand command, CancellationToken cancellationToken)
    {
        if (_runTask is not null && !_runTask.IsCompleted)
        {
            return ErrorEvent("A run is already active.");
        }

        var bot = command.Bot ?? BotKind.TradingPost;
        var mode = command.Mode ?? RunMode.DryRun;
        var profile = command.Profile ?? "balanced";
        _runId = Guid.NewGuid().ToString("N");
        await _store.StartRunAsync(_runId, bot.ToString(), mode.ToString(), profile, cancellationToken);
        _runCancellation = new CancellationTokenSource();
        SetStatus(WorkerState.Starting, bot, mode, profile, "Starting", "Run requested.");

        _runTask = RunAsync(_runId, bot, mode, profile, _runCancellation.Token);
        await Task.Yield();
        return StatusEvent(command.RequestId);
    }

    private WorkerEvent Pause()
    {
        if (_status.State is WorkerState.Starting or WorkerState.TradingPostReady or WorkerState.ProcessingTrading or WorkerState.Fishing)
        {
            SetStatus(WorkerState.Paused, message: "Pause requested; no new actions will begin.");
        }

        return StatusEvent();
    }

    private WorkerEvent Resume()
    {
        if (_status.State == WorkerState.Paused)
        {
            SetStatus(_status.Bot == BotKind.Fishing ? WorkerState.Fishing : WorkerState.ProcessingTrading, message: "Run resumed.");
        }

        return StatusEvent();
    }

    private async Task<WorkerEvent> StopCommandAsync(CancellationToken cancellationToken)
    {
        await StopAsync(cancellationToken);
        return StatusEvent();
    }

    private async Task<WorkerEvent> EmergencyStopAsync(CancellationToken cancellationToken)
    {
        _runCancellation?.Cancel();
        await StopAsync(cancellationToken);
        SetStatus(WorkerState.Stopped, message: "Emergency stop completed.");
        return StatusEvent();
    }

    private async Task RunAsync(string runId, BotKind bot, RunMode mode, string profile, CancellationToken cancellationToken)
    {
        try
        {
            if (mode == RunMode.Live)
            {
                SetStatus(WorkerState.NeedsAttention, bot, mode, profile, "Live mode unavailable", "The native compatibility adapter has not been enabled yet.");
                return;
            }

            SetStatus(bot == BotKind.Fishing ? WorkerState.Fishing : WorkerState.TradingPostReady, bot, mode, profile, "Ready", "Dry-run worker is ready.");
            while (!cancellationToken.IsCancellationRequested)
            {
                if (_status.State == WorkerState.Paused)
                {
                    await Task.Delay(100, cancellationToken);
                    continue;
                }

                SetStatus(bot == BotKind.Fishing ? WorkerState.Fishing : WorkerState.ProcessingTrading, bot, mode, profile, "Heartbeat", "No live input is issued by the scaffold.");
                await _store.SaveCheckpointAsync(
                    runId,
                    state: _status.State.ToString(),
                    payload: new { bot, mode, profile, timestamp = DateTimeOffset.UtcNow },
                    cancellationToken);
                Emit(new WorkerEvent(WorkerEventTypes.Health, JsonSerializer.Serialize(new { state = _status.State, timestamp = DateTimeOffset.UtcNow })));
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Worker run cancelled.");
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Worker run failed.");
            SetStatus(WorkerState.Faulted, bot, mode, profile, "Run failed", exception.Message);
            Emit(new WorkerEvent(WorkerEventTypes.Error, exception.ToString()));
        }
    }

    private WorkerEvent StatusEvent(string? correlationId = null) =>
        new(WorkerEventTypes.Status, JsonSerializer.Serialize(_status), correlationId, DateTimeOffset.UtcNow);

    private WorkerEvent ErrorEvent(string message)
    {
        _logger.LogWarning("{Message}", message);
        var result = new WorkerEvent(WorkerEventTypes.Error, message, Timestamp: DateTimeOffset.UtcNow);
        Emit(result);
        return result;
    }

    private void SetStatus(
        WorkerState state,
        BotKind? bot = null,
        RunMode? mode = null,
        string? profile = null,
        string? lastAction = null,
        string? message = null)
    {
        _status = _status with
        {
            State = state,
            Bot = bot ?? _status.Bot,
            Mode = mode ?? _status.Mode,
            Profile = profile ?? _status.Profile,
            LastAction = lastAction ?? _status.LastAction,
            Timestamp = DateTimeOffset.UtcNow,
            Message = message
        };
        Emit(StatusEvent());
    }

    private void Emit(WorkerEvent workerEvent) => EventRaised?.Invoke(this, workerEvent);
}
