using Mmopal.Contracts;

namespace Mmopal.Native;

public interface IWindowController
{
    Task<bool> EnsureWindowAsync(string processName, int width, int height, CancellationToken cancellationToken = default);
}

public interface IProcessController
{
    bool IsRunning(string executableName);
    Task<bool> LaunchAsync(string executablePath, string? arguments, CancellationToken cancellationToken = default);
    Task StopAsync(string executableName, CancellationToken cancellationToken = default);
}

public interface IScreenshotProvider
{
    Task<AutomationFrame> CaptureAsync(CaptureRegion region, CancellationToken cancellationToken = default);
}

public interface ITemplateMatcher
{
    Task<TemplateMatch?> FindAsync(AutomationFrame frame, TemplateDefinition template, CancellationToken cancellationToken = default);
}

public interface IOcrReader
{
    Task<OcrResult?> ReadAsync(AutomationFrame frame, CaptureRegion region, CancellationToken cancellationToken = default);
}

public interface IInputController
{
    Task ClickAsync(int x, int y, CancellationToken cancellationToken = default);
    Task KeyPressAsync(string key, CancellationToken cancellationToken = default);
    Task SendTextAsync(string text, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}

public interface IArduinoInput
{
    bool IsAvailable { get; }
    Task<bool> ConnectAsync(string port, int baudRate, CancellationToken cancellationToken = default);
    Task ClickAsync(int x, int y, CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
}

public interface IGw2DataProvider
{
    Task<IReadOnlyList<Transaction>> GetTransactionsAsync(CancellationToken cancellationToken = default);
    Task<AccountBalance?> GetBalanceAsync(CancellationToken cancellationToken = default);
}

public sealed record CaptureRegion(int X, int Y, int Width, int Height)
{
    public static CaptureRegion FullScreen1024x768 => new(0, 0, 1024, 768);
}

public sealed record TemplateDefinition(string Name, string FilePath, CaptureRegion? Region = null, decimal MatchScore = 0.8m);

public sealed record AutomationFrame(DateTimeOffset CapturedAt, CaptureRegion Region, byte[] Pixels);

public sealed record TemplateMatch(int X, int Y, int Width, int Height, decimal Score);

public sealed record OcrResult(string Text, decimal Confidence);

public sealed class NativeRuntimeLayout
{
    public NativeRuntimeLayout(string root)
    {
        Root = Path.GetFullPath(root);
        HostExecutable = Path.Combine(Root, "mmopal.exe");
        NativeCore = Path.Combine(Root, "core.dll");
        RuntimeConfig = Path.Combine(Root, "mmopal.runtimeconfig.json");
    }

    public string Root { get; }
    public string HostExecutable { get; }
    public string NativeCore { get; }
    public string RuntimeConfig { get; }

    public NativeRuntimeReport Inspect()
    {
        var missing = new List<string>();
        if (!File.Exists(HostExecutable)) missing.Add(HostExecutable);
        if (!File.Exists(NativeCore)) missing.Add(NativeCore);
        if (!File.Exists(RuntimeConfig)) missing.Add(RuntimeConfig);

        return new NativeRuntimeReport(
            IsPresent: missing.Count == 0,
            Root,
            HostExecutable,
            NativeCore,
            RuntimeConfig,
            missing);
    }
}

public sealed record NativeRuntimeReport(
    bool IsPresent,
    string Root,
    string HostExecutable,
    string NativeCore,
    string RuntimeConfig,
    IReadOnlyList<string> MissingFiles);

public sealed class DryRunInputController : IInputController
{
    private readonly Action<string> _trace;

    public DryRunInputController(Action<string>? trace = null) => _trace = trace ?? (_ => { });

    public Task ClickAsync(int x, int y, CancellationToken cancellationToken = default)
    {
        _trace($"DRY-RUN click at {x},{y}");
        return Task.CompletedTask;
    }

    public Task KeyPressAsync(string key, CancellationToken cancellationToken = default)
    {
        _trace($"DRY-RUN keypress {key}");
        return Task.CompletedTask;
    }

    public Task SendTextAsync(string text, CancellationToken cancellationToken = default)
    {
        _trace($"DRY-RUN text input ({text.Length} characters)");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        _trace("DRY-RUN input stopped");
        return Task.CompletedTask;
    }
}
