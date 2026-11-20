namespace Mmopal.Contracts;

public sealed record WorkerCommand(
    string Type,
    string? RequestId = null,
    BotKind? Bot = null,
    RunMode? Mode = null,
    string? Profile = null,
    string? Payload = null);

public sealed record WorkerEvent(
    string Type,
    string Payload,
    string? CorrelationId = null,
    DateTimeOffset? Timestamp = null);

public static class WorkerCommandTypes
{
    public const string Start = "start";
    public const string Pause = "pause";
    public const string Resume = "resume";
    public const string Stop = "stop";
    public const string EmergencyStop = "emergency-stop";
    public const string Status = "status";
    public const string Shutdown = "shutdown";
}

public static class WorkerEventTypes
{
    public const string Status = "status";
    public const string Log = "log";
    public const string Error = "error";
    public const string Health = "health";
    public const string SessionReport = "session-report";
}
