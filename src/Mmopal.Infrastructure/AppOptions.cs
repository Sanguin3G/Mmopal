namespace Mmopal.Infrastructure;

public sealed class MmopalOptions
{
    public string? DataRoot { get; set; }
    public string WorkerPipeName { get; set; } = "Mmopal.Worker";
    public string DefaultProfile { get; set; } = "balanced";
}

public sealed class RunProfile
{
    public string Name { get; set; } = "balanced";
    public string RunMode { get; set; } = "DryRun";
    public string Speed { get; set; } = "Balanced";
    public int MaxRetries { get; set; } = 3;
    public int ActionTimeoutSeconds { get; set; } = 10;
    public int ApiTransactionRefreshSeconds { get; set; } = 60;
    public int ApiBalanceRefreshSeconds { get; set; } = 120;
    public int OverbidScanIntervalSeconds { get; set; } = 30;
    public int MaxOverbidUpdatesPerPass { get; set; } = 3;
}
