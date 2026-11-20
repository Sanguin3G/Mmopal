using System.Runtime.InteropServices;

namespace Mmopal.Native;

/// <summary>
/// Keeps Windows from sleeping, turning the display off, or starting the screensaver/lock while a live session
/// runs: any of those stops screen capture and input to Guild Wars 2. Resets the idle timers every 30 seconds
/// from a timer (no ES_CONTINUOUS, so nothing is left set when the session ends or the process exits).
/// </summary>
public sealed class KeepAwake : IDisposable
{
    private const uint EsSystemRequired = 0x00000001;
    private const uint EsDisplayRequired = 0x00000002;
    private readonly Timer _timer;

    public KeepAwake()
    {
        _timer = new Timer(_ => Ping(), null, TimeSpan.Zero, TimeSpan.FromSeconds(30));
    }

    public static void Ping()
    {
        if (OperatingSystem.IsWindows())
        {
            _ = SetThreadExecutionState(EsSystemRequired | EsDisplayRequired);
        }
    }

    public void Dispose() => _timer.Dispose();

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);
}
