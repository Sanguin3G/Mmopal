using System.Text.Json;
using Microsoft.Extensions.Logging;
using Mmopal.Contracts;
using Mmopal.Gw2;
using Mmopal.Infrastructure;
using Mmopal.Native;

namespace Mmopal.Worker;

/// <summary>Status and events sent to the desktop app.</summary>
public sealed partial class WorkerRuntime
{
    private WorkerEvent StatusEvent(string? correlationId = null) => CreateStatusEvent(Status, correlationId);

    private static WorkerEvent CreateStatusEvent(WorkerStatus status, string? correlationId = null) =>
        new(WorkerEventTypes.Status, JsonSerializer.Serialize(status), correlationId, DateTimeOffset.UtcNow);

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
        WorkerEvent statusEvent;
        lock (_statusLock)
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
            statusEvent = CreateStatusEvent(_status);
        }

        Emit(statusEvent);
    }

    private void SetStatusIfNotPaused(
        WorkerState state,
        BotKind? bot = null,
        RunMode? mode = null,
        string? profile = null,
        string? lastAction = null,
        string? message = null)
    {
        WorkerEvent statusEvent;
        lock (_statusLock)
        {
            _status = _status with
            {
                State = _pauseRequested ? WorkerState.Paused : state,
                Bot = bot ?? _status.Bot,
                Mode = mode ?? _status.Mode,
                Profile = profile ?? _status.Profile,
                LastAction = lastAction ?? _status.LastAction,
                Timestamp = DateTimeOffset.UtcNow,
                Message = _pauseRequested ? "Paused; cycle completed and no new actions will begin." : message
            };
            statusEvent = CreateStatusEvent(_status);
        }

        Emit(statusEvent);
    }

    private void Emit(WorkerEvent workerEvent) => EventRaised?.Invoke(this, workerEvent);
}
