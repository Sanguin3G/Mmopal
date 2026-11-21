using Mmopal.Gw2;
using Mmopal.Native;

namespace Mmopal.Worker;

public sealed class Gw2ClientPreparer : IGw2ClientPreparer
{
    private readonly IProcessController _processes;
    private readonly IWindowController _windows;
    private readonly TimeSpan _windowWaitTimeout;
    private readonly bool _hasExplicitTimeout;
    private readonly TimeSpan _pollInterval;

    public Gw2ClientPreparer(
        IProcessController? processes = null,
        IWindowController? windows = null,
        TimeSpan? windowWaitTimeout = null,
        TimeSpan? pollInterval = null)
    {
        _processes = processes ?? new Win32ProcessController();
        _windows = windows ?? new Win32WindowController();
        _hasExplicitTimeout = windowWaitTimeout.HasValue;
        _windowWaitTimeout = windowWaitTimeout ?? TimeSpan.FromSeconds(45);
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(500);
    }

    public async Task<Gw2ClientPrepareResult> PrepareAsync(
        Gw2ClientSettings settings,
        bool allowLaunch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var width = settings.WindowWidth > 0 ? settings.WindowWidth : 1024;
        var height = settings.WindowHeight > 0 ? settings.WindowHeight : 768;
        var waitTimeout = _hasExplicitTimeout
            ? _windowWaitTimeout
            : TimeSpan.FromSeconds(Math.Clamp(settings.WindowWaitTimeoutSeconds, 5, 180));
        var maxAttempts = Math.Clamp(settings.MaxLaunchAttempts, 1, 5);
        var alreadyRunning = _processes.IsRunning(Gw2ClientSettings.DefaultExecutableName);

        if (alreadyRunning)
        {
            var runningTimeout = _hasExplicitTimeout ? waitTimeout : TimeSpan.FromSeconds(3);
            if (await WaitForWindowAsync(width, height, runningTimeout, cancellationToken).ConfigureAwait(false))
            {
                return new Gw2ClientPrepareResult(
                    WindowReady: true,
                    Launched: false,
                    AlreadyRunning: true,
                    Message: $"Positioned Guild Wars 2 at top-left (0, 0) sized {width}x{height}.");
            }

            return new Gw2ClientPrepareResult(
                WindowReady: false,
                Launched: false,
                AlreadyRunning: true,
                Message: $"Guild Wars 2 is running, but no visible game window was found to position at {width}x{height} (launcher or 2FA may be waiting).");
        }

        if (!allowLaunch)
        {
            return new Gw2ClientPrepareResult(
                WindowReady: false,
                Launched: false,
                AlreadyRunning: false,
                Message: "Guild Wars 2 is not running and auto-launch is disabled.");
        }

        var targetPath = settings.ResolveEffectiveLaunchTarget(Gw2InstallationLocator.DetectDefaultExecutablePath);
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return new Gw2ClientPrepareResult(
                WindowReady: false,
                Launched: false,
                AlreadyRunning: false,
                Message: "Guild Wars 2 executable path is not set and could not be auto-detected.");
        }

        var isUriLaunch = Win32ProcessController.IsUriLaunch(targetPath);
        var platformLabel = settings.IsSteamPlatform || isUriLaunch ? "via Steam" : "standalone";
        var launchArguments = isUriLaunch ? null : EffectiveLaunchArguments(settings);
        var attemptsUsed = 0;
        var launchedAny = false;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            attemptsUsed = attempt;
            cancellationToken.ThrowIfCancellationRequested();

            bool started;
            try
            {
                started = await _processes.LaunchAsync(targetPath, launchArguments, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new Gw2ClientPrepareResult(
                    WindowReady: false,
                    Launched: launchedAny,
                    AlreadyRunning: false,
                    Message: $"Failed to launch Guild Wars 2 ({platformLabel}) from '{targetPath}' on attempt {attempt}/{maxAttempts}: {ex.Message}");
            }

            if (!started)
            {
                if (attempt < maxAttempts)
                {
                    await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                return new Gw2ClientPrepareResult(
                    WindowReady: false,
                    Launched: launchedAny,
                    AlreadyRunning: false,
                    Message: $"Failed to launch Guild Wars 2 ({platformLabel}) from '{targetPath}' after {attemptsUsed} attempt(s).");
            }

            launchedAny = true;
            if (await WaitForWindowAsync(width, height, waitTimeout, cancellationToken).ConfigureAwait(false))
            {
                if (!_hasExplicitTimeout)
                {
                    // Gw2-64.exe initially opens a small "Downloading 0KB (0KB/sec)" splash window before
                    // replacing it with the full Launcher HWND. Re-snap during this transition window.
                    var transitionDeadline = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(2400);
                    while (DateTimeOffset.UtcNow < transitionDeadline)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await Task.Delay(350, cancellationToken).ConfigureAwait(false);
                        _ = await _windows.EnsureWindowAsync(
                            Gw2ClientSettings.DefaultWindowName,
                            width,
                            height,
                            cancellationToken).ConfigureAwait(false);
                    }
                }

                return new Gw2ClientPrepareResult(
                    WindowReady: true,
                    Launched: true,
                    AlreadyRunning: false,
                    Message: $"Launched Guild Wars 2 ({platformLabel}) and positioned at top-left (0, 0) sized {width}x{height}.");
            }
        }

        return new Gw2ClientPrepareResult(
            WindowReady: false,
            Launched: launchedAny,
            AlreadyRunning: false,
            Message: $"Guild Wars 2 ({platformLabel}) did not open a ready {width}x{height} window after {attemptsUsed} launch attempt(s) ({waitTimeout.TotalSeconds:0}s timeout). Check if the launcher needs manual login, 2FA, or patching.");
    }

    public async Task<Gw2ClientPrepareResult> RestartAsync(
        Gw2ClientSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (_processes.IsRunning(Gw2ClientSettings.DefaultExecutableName))
        {
            await StopAndWaitForExitAsync(cancellationToken).ConfigureAwait(false);
            if (!_hasExplicitTimeout)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
        }

        return await PrepareAsync(settings, allowLaunch: true, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Gw2ClientPrepareResult> CloseGameAsync(
        Gw2ClientSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!_processes.IsRunning(Gw2ClientSettings.DefaultExecutableName))
        {
            return new Gw2ClientPrepareResult(
                WindowReady: false,
                Launched: false,
                AlreadyRunning: false,
                Message: "Guild Wars 2 is already closed.");
        }

        await StopAndWaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var stillRunning = _processes.IsRunning(Gw2ClientSettings.DefaultExecutableName);
        return new Gw2ClientPrepareResult(
            WindowReady: stillRunning,
            Launched: false,
            AlreadyRunning: stillRunning,
            Message: stillRunning
                ? "Attempted to close Guild Wars 2, but process is still shutting down."
                : "Closed Guild Wars 2 cleanly.");
    }

    private static string EffectiveLaunchArguments(Gw2ClientSettings settings) =>
        string.IsNullOrWhiteSpace(settings.LaunchArguments)
            ? "-autologin -windowed"
            : settings.LaunchArguments.Trim();

    /// <summary>Stops the game and polls until it has exited, for up to 5 s (one poll interval in tests).</summary>
    private async Task StopAndWaitForExitAsync(CancellationToken cancellationToken)
    {
        await _processes.StopAsync(Gw2ClientSettings.DefaultExecutableName, cancellationToken).ConfigureAwait(false);
        var deadline = DateTimeOffset.UtcNow + (_hasExplicitTimeout ? _pollInterval : TimeSpan.FromSeconds(5));
        while (_processes.IsRunning(Gw2ClientSettings.DefaultExecutableName) && DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<bool> WaitForWindowAsync(
        int width,
        int height,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await _windows.EnsureWindowAsync(Gw2ClientSettings.DefaultWindowName, width, height, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                return false;
            }

            await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
        }
    }
}
