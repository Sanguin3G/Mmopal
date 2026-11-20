using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Mmopal.Native;

public sealed partial class Win32WindowController : IWindowController
{
    private const int SwRestore = 9;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpShowWindow = 0x0040;
    private const string LauncherWindowClass = "ArenaNet";
    private static readonly string[] PreferredGw2WindowClasses =
    [
        "ArenaNet_Gr_Window_Class",
        "ArenaNet_Dx_Window_Class"
    ];

    public Gw2WindowInspection? InspectWindow(string processName = "Guild Wars 2") =>
        string.IsNullOrWhiteSpace(processName) ? null : InspectTargetWindow(processName);

    internal static Gw2WindowInspection? InspectTargetWindow(string processOrTitle)
    {
        var handle = FindTargetWindow(processOrTitle);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        var title = ReadWindowText(handle);
        var className = ReadClassName(handle);
        _ = GetWindowRect(handle, out var rect);
        var is3D = PreferredGw2WindowClasses.Any(c => string.Equals(className, c, StringComparison.OrdinalIgnoreCase));
        var isLauncher = !is3D &&
                         (string.Equals(className, LauncherWindowClass, StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(title, "Guild Wars 2", StringComparison.OrdinalIgnoreCase));
        return new Gw2WindowInspection(
            handle,
            title,
            className,
            isLauncher,
            is3D,
            rect.Left,
            rect.Top,
            Math.Max(0, rect.Right - rect.Left),
            Math.Max(0, rect.Bottom - rect.Top));
    }

    public Task<bool> EnsureWindowAsync(string processName, int width, int height, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(processName))
        {
            throw new ArgumentException("A process or window name is required.", nameof(processName));
        }

        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Window width must be positive.");
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), "Window height must be positive.");
        }

        var handle = FindTargetWindow(processName);
        if (handle == IntPtr.Zero)
        {
            return Task.FromResult(false);
        }

        var positioned = PositionAndResize(handle, x: 0, y: 0, width, height);
        return Task.FromResult(positioned);
    }

    internal static IntPtr FindTargetWindow(string processOrTitle)
    {
        var target = processOrTitle.Trim();
        var processNames = ResolveCandidateProcessNames(target);
        var pids = new HashSet<uint>();
        foreach (var candidateProcess in processNames)
        {
            var processes = Process.GetProcessesByName(candidateProcess);
            try
            {
                foreach (var process in processes)
                {
                    pids.Add((uint)process.Id);
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

        var candidates = new List<WindowCandidate>();
        EnumWindows((hwnd, lParam) =>
        {
            if (!IsWindowVisible(hwnd))
            {
                return true;
            }

            _ = GetWindowThreadProcessId(hwnd, out var pid);
            var title = ReadWindowText(hwnd);
            var className = ReadClassName(hwnd);
            var matchesProcess = pids.Count > 0 && pids.Contains(pid);
            var matchesTitle = !string.IsNullOrWhiteSpace(title) &&
                               string.Equals(title, target, StringComparison.OrdinalIgnoreCase);

            if (matchesProcess || (pids.Count == 0 && matchesTitle))
            {
                _ = GetWindowRect(hwnd, out var rect);
                var w = Math.Max(0, rect.Right - rect.Left);
                var h = Math.Max(0, rect.Bottom - rect.Top);
                candidates.Add(new WindowCandidate(hwnd, pid, title, className, matchesProcess, w, h));
            }

            return true;
        }, IntPtr.Zero);

        return SelectBestWindow(candidates, target);
    }

    internal static IntPtr SelectBestWindow(IReadOnlyList<WindowCandidate> candidates, string target)
    {
        if (candidates.Count == 0)
        {
            return IntPtr.Zero;
        }

        // Ignore the small pre-launcher "Downloading 0KB (0KB/sec)" splash notification window (~690x420)
        // so WaitForWindowAsync waits for and snaps the real GW2 Launcher (~1010x877) or 3D Game window (1024x768).
        var eligible = candidates
            .Where(c => !c.IsUndersizedSplashWindow)
            .OrderByDescending(c => c.Area)
            .ToArray();
        if (eligible.Length == 0)
        {
            return IntPtr.Zero;
        }

        // Prefer the actual Guild Wars 2 game rendering window over the launcher or any other window.
        foreach (var preferredClass in PreferredGw2WindowClasses)
        {
            var byClass = eligible.FirstOrDefault(candidate =>
                candidate.MatchesProcess &&
                string.Equals(candidate.ClassName, preferredClass, StringComparison.OrdinalIgnoreCase));
            if (byClass is not null)
            {
                return byClass.Handle;
            }
        }

        // Next prefer a window owned by the target process whose title matches the requested name.
        var byProcessAndTitle = eligible.FirstOrDefault(candidate =>
            candidate.MatchesProcess &&
            string.Equals(candidate.Title, target, StringComparison.OrdinalIgnoreCase));
        if (byProcessAndTitle is not null)
        {
            return byProcessAndTitle.Handle;
        }

        // Next prefer any titled visible window owned by the target process.
        var byProcess = eligible.FirstOrDefault(candidate =>
            candidate.MatchesProcess && !string.IsNullOrWhiteSpace(candidate.Title));
        if (byProcess is not null)
        {
            return byProcess.Handle;
        }

        return eligible[0].Handle;
    }

    internal static IReadOnlyList<string> ResolveCandidateProcessNames(string processOrTitle)
    {
        var normalized = Win32ProcessController.NormalizeProcessName(processOrTitle);
        if (string.Equals(processOrTitle, "Guild Wars 2", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "Gw2-64", StringComparison.OrdinalIgnoreCase))
        {
            return ["Gw2-64", "Gw2"];
        }

        return [normalized];
    }

    internal static bool PositionAndResize(IntPtr handle, int x, int y, int width, int height)
    {
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        if (IsIconic(handle))
        {
            _ = ShowWindow(handle, SwRestore);
        }

        var className = ReadClassName(handle);
        var isLauncher = string.Equals(className, LauncherWindowClass, StringComparison.OrdinalIgnoreCase);
        var moved = isLauncher
            ? SetWindowPos(handle, IntPtr.Zero, x, y, 0, 0, SwpNoSize | SwpNoZOrder | SwpShowWindow)
            : SetWindowPos(handle, IntPtr.Zero, x, y, width, height, SwpNoZOrder | SwpShowWindow);
        ForceForeground(handle);
        return moved;
    }

    internal static void ForceForeground(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var fg = GetForegroundWindow();
        if (fg == handle)
        {
            return;
        }

        var curThread = GetCurrentThreadId();
        var fgThread = fg != IntPtr.Zero ? GetWindowThreadProcessId(fg, out _) : 0;
        var targetThread = GetWindowThreadProcessId(handle, out _);
        var attachedFg = false;
        var attachedTarget = false;
        try
        {
            if (fgThread != 0 && fgThread != curThread)
            {
                attachedFg = AttachThreadInput(curThread, fgThread, true);
            }

            if (targetThread != 0 && targetThread != curThread)
            {
                attachedTarget = AttachThreadInput(curThread, targetThread, true);
            }

            _ = BringWindowToTop(handle);
            _ = SetForegroundWindow(handle);
            SwitchToThisWindow(handle, true);
            _ = SetActiveWindow(handle);
            _ = SetFocus(handle);
        }
        finally
        {
            if (attachedTarget) _ = AttachThreadInput(curThread, targetThread, false);
            if (attachedFg) _ = AttachThreadInput(curThread, fgThread, false);
        }
    }

    private static string ReadWindowText(IntPtr hwnd)
    {
        var length = GetWindowTextLengthW(hwnd);
        if (length <= 0)
        {
            return string.Empty;
        }

        var buffer = new StringBuilder(length + 1);
        _ = GetWindowTextW(hwnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private static string ReadClassName(IntPtr hwnd)
    {
        var buffer = new StringBuilder(256);
        var length = GetClassNameW(hwnd, buffer, buffer.Capacity);
        return length > 0 ? buffer.ToString(0, length) : string.Empty;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    internal sealed record WindowCandidate(
        IntPtr Handle,
        uint ProcessId,
        string Title,
        string ClassName,
        bool MatchesProcess,
        int Width = 0,
        int Height = 0)
    {
        public long Area => (long)Math.Max(0, Width) * Math.Max(0, Height);

        public bool Is3DGameWindow =>
            PreferredGw2WindowClasses.Any(c => string.Equals(ClassName, c, StringComparison.OrdinalIgnoreCase));

        public bool IsUndersizedSplashWindow =>
            !Is3DGameWindow &&
            Width > 0 &&
            Height > 0 &&
            (Width < 780 || Height < 540);
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetActiveWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern void SwitchToThisWindow(IntPtr hWnd, [MarshalAs(UnmanagedType.Bool)] bool fAltTab);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect lpRect);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLengthW(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
}
