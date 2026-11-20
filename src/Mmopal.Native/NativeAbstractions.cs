using Mmopal.Contracts;

namespace Mmopal.Native;

public sealed record Gw2WindowInspection(
    IntPtr Handle,
    string Title,
    string ClassName,
    bool IsLauncherWindow,
    bool IsGame3DWindow,
    int Left,
    int Top,
    int Width,
    int Height);

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

public interface IArduinoInput
{
    bool IsAvailable { get; }
    Task<bool> ConnectAsync(string port, int baudRate, CancellationToken cancellationToken = default);
    Task ClickAsync(int x, int y, CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
}

public sealed class DryRunInputController : IInputController
{
    private readonly Action<string> _trace;

    public DryRunInputController(Action<string>? trace = null) => _trace = trace ?? (_ => { });

    public Task ClickAsync(int x, int y, CancellationToken cancellationToken = default)
    {
        _trace($"DRY-RUN click at {x},{y}");
        return Task.CompletedTask;
    }

    public Task DoubleClickAsync(int x, int y, CancellationToken cancellationToken = default)
    {
        _trace($"DRY-RUN double-click at {x},{y}");
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
