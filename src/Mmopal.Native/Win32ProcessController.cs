using System.Diagnostics;

namespace Mmopal.Native;

public sealed class Win32ProcessController : IProcessController
{
    public bool IsRunning(string executableName)
    {
        if (string.IsNullOrWhiteSpace(executableName))
        {
            return false;
        }

        var normalized = NormalizeProcessName(executableName);
        var processes = Process.GetProcessesByName(normalized);
        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    public Task<bool> LaunchAsync(string executablePath, string? arguments, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new ArgumentException("An executable path or steam:// URI is required.", nameof(executablePath));
        }

        var trimmed = Environment.ExpandEnvironmentVariables(executablePath.Trim());
        if (IsUriLaunch(trimmed))
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = trimmed,
                UseShellExecute = true
            };
            Process.Start(startInfo);
            return Task.FromResult(true);
        }

        var fullPath = Path.GetFullPath(trimmed);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Game executable was not found at '{fullPath}'.", fullPath);
        }

        var processStartInfo = new ProcessStartInfo
        {
            FileName = fullPath,
            Arguments = arguments?.Trim() ?? string.Empty,
            WorkingDirectory = Path.GetDirectoryName(fullPath) ?? string.Empty,
            UseShellExecute = false
        };

        using var process = Process.Start(processStartInfo);
        return Task.FromResult(process is not null);
    }

    public async Task StopAsync(string executableName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(executableName))
        {
            return;
        }

        var normalized = NormalizeProcessName(executableName);
        var currentSessionId = Process.GetCurrentProcess().SessionId;
        var processes = Process.GetProcessesByName(normalized);
        try
        {
            foreach (var process in processes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (process.SessionId != currentSessionId || process.HasExited)
                    {
                        continue;
                    }

                    if (process.CloseMainWindow())
                    {
                        using var grace = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        grace.CancelAfter(TimeSpan.FromSeconds(5));
                        try
                        {
                            await process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                        {
                            // Fall through to Kill when the window does not close cleanly.
                        }
                    }

                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                    // Process may have exited or denied access concurrently.
                }
            }
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    internal static string NormalizeProcessName(string executableName)
    {
        var trimmed = executableName.Trim();
        var fileName = Path.GetFileName(trimmed);
        return fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^4]
            : fileName;
    }

    public static bool IsUriLaunch(string target) =>
        target.StartsWith("steam://", StringComparison.OrdinalIgnoreCase);
}
