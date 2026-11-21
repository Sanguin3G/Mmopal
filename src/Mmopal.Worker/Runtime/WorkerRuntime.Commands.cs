using System.Text.Json;
using Microsoft.Extensions.Logging;
using Mmopal.Contracts;
using Mmopal.Gw2;
using Mmopal.Infrastructure;
using Mmopal.Native;

namespace Mmopal.Worker;

/// <summary>Commands from the desktop app: start, pause, resume, graceful Stop, Emergency stop.</summary>
public sealed partial class WorkerRuntime
{
    public async Task<WorkerEvent> HandleAsync(WorkerCommand command, CancellationToken cancellationToken = default)
    {
        if (command is null || string.IsNullOrWhiteSpace(command.Type))
        {
            return ErrorEvent("Worker command type is required.");
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            return command.Type.ToLowerInvariant() switch
            {
                WorkerCommandTypes.Start => await StartAsync(command, cancellationToken),
                WorkerCommandTypes.Pause => Pause(),
                WorkerCommandTypes.Resume => Resume(),
                WorkerCommandTypes.Stop => await GracefulStopAsync(cancellationToken),
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
        lock (_statusLock)
        {
            _pauseRequested = false;
            _stopRequested = false;
        }

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
        lock (_statusLock)
        {
            _pauseRequested = false;
            _stopRequested = false;
        }

        SetStatus(WorkerState.Starting, bot, mode, profile, "Starting", "Run requested.");

        _runTask = RunAsync(_runId, bot, mode, profile, command.Payload, _runCancellation.Token);
        await Task.Yield();
        return StatusEvent(command.RequestId);
    }

    private WorkerEvent Pause()
    {
        WorkerEvent? statusEvent = null;
        lock (_statusLock)
        {
            if (_status.State is WorkerState.Starting
                or WorkerState.LaunchingClient
                or WorkerState.WaitingForLogin
                or WorkerState.TradingPostReady
                or WorkerState.ProcessingTrading
                or WorkerState.Recovering
                or WorkerState.Fishing)
            {
                _pauseRequested = true;
                _status = _status with
                {
                    State = WorkerState.Paused,
                    Timestamp = DateTimeOffset.UtcNow,
                    Message = "Pause requested; no new actions will begin."
                };
                statusEvent = CreateStatusEvent(_status);
            }
        }

        if (statusEvent is not null)
        {
            Emit(statusEvent);
        }

        return StatusEvent();
    }

    private WorkerEvent Resume()
    {
        WorkerEvent? statusEvent = null;
        lock (_statusLock)
        {
            if (_status.State == WorkerState.Paused || _pauseRequested)
            {
                _pauseRequested = false;
                var nextState = _status.Bot == BotKind.Fishing ? WorkerState.Fishing : WorkerState.ProcessingTrading;
                _status = _status with
                {
                    State = nextState,
                    Timestamp = DateTimeOffset.UtcNow,
                    Message = "Run resumed."
                };
                statusEvent = CreateStatusEvent(_status);
            }
        }

        if (statusEvent is not null)
        {
            Emit(statusEvent);
        }

        return StatusEvent();
    }

    private async Task<WorkerEvent> StopCommandAsync(CancellationToken cancellationToken)
    {
        await StopAsync(cancellationToken);
        return StatusEvent();
    }

    /// <summary>
    /// Stop: let the current step finish (the loop leaves at its next safe point and saves the session report), then
    /// stop. Returns at once so Emergency stop still works meanwhile; after 90 s it stops anyway.
    /// </summary>
    private async Task<WorkerEvent> GracefulStopAsync(CancellationToken cancellationToken)
    {
        var runTask = _runTask;
        if (runTask is null || runTask.IsCompleted)
        {
            await StopAsync(cancellationToken);
            return StatusEvent();
        }

        lock (_statusLock)
        {
            _stopRequested = true;
        }

        _logger.LogInformation("Stop requested: finishing the current step, then stopping.");
        SetStatus(Status.State, message: "Stopping after the current step…");

        // Between steps the loop leaves within a moment: finish inline. Mid-step, hand off so the command gate stays
        // free for Emergency stop.
        try
        {
            await runTask.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
            await StopAsync(cancellationToken);
            return StatusEvent();
        }
        catch (TimeoutException)
        {
        }
        catch (Exception) when (runTask.IsCompleted)
        {
            await StopAsync(cancellationToken);
            return StatusEvent();
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await runTask.WaitAsync(GracefulStopTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("The current step did not finish within {Seconds} s; stopping now.", (int)GracefulStopTimeout.TotalSeconds);
            }
            catch (Exception)
            {
                // The run ended with an error; it is reported by the run itself.
            }

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (ReferenceEquals(_runTask, runTask))
                {
                    await StopAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
            finally
            {
                _gate.Release();
            }
        });
        return StatusEvent();
    }

    private async Task<WorkerEvent> EmergencyStopAsync(CancellationToken cancellationToken)
    {
        _runCancellation?.Cancel();
        await StopAsync(cancellationToken);
        SetStatus(WorkerState.Stopped, message: "Emergency stop completed.");
        return StatusEvent();
    }
}
