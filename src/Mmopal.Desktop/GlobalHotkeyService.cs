using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Mmopal.Desktop;

[Flags]
public enum HotkeyModifiers : uint
{
    None = 0x0000,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Win = 0x0008,
    NoRepeat = 0x4000
}

public sealed record HotkeyGesture(HotkeyModifiers Modifiers, uint VirtualKey, string CanonicalText)
{
    public static bool TryParse(string? text, out HotkeyGesture? gesture, out string? error)
    {
        gesture = null;
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return true;
        }

        var modifiers = HotkeyModifiers.None;
        string? keyToken = null;

        foreach (var raw in parts)
        {
            var token = raw.Trim();
            if (token.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("Control", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= HotkeyModifiers.Control;
            }
            else if (token.Equals("Alt", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= HotkeyModifiers.Alt;
            }
            else if (token.Equals("Shift", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= HotkeyModifiers.Shift;
            }
            else if (token.Equals("Win", StringComparison.OrdinalIgnoreCase) ||
                     token.Equals("Windows", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= HotkeyModifiers.Win;
            }
            else
            {
                if (keyToken is not null)
                {
                    error = $"Hotkey '{text}' specifies more than one key.";
                    return false;
                }

                keyToken = token;
            }
        }

        if (keyToken is null)
        {
            error = $"Hotkey '{text}' must include a key (for example Ctrl+Shift+P).";
            return false;
        }

        if (modifiers == HotkeyModifiers.None)
        {
            error = $"Global hotkey '{text}' must include at least one modifier (Ctrl, Alt, Shift, or Win).";
            return false;
        }

        if (!TryMapVirtualKey(keyToken, out var vk, out var normalizedKey))
        {
            error = $"Unsupported hotkey key '{keyToken}'. Use A-Z, 0-9, F1-F12, Space, Pause, Home, End, Insert, or Delete.";
            return false;
        }

        var segments = new List<string>(5);
        if (modifiers.HasFlag(HotkeyModifiers.Control)) segments.Add("Ctrl");
        if (modifiers.HasFlag(HotkeyModifiers.Alt)) segments.Add("Alt");
        if (modifiers.HasFlag(HotkeyModifiers.Shift)) segments.Add("Shift");
        if (modifiers.HasFlag(HotkeyModifiers.Win)) segments.Add("Win");
        segments.Add(normalizedKey);

        gesture = new HotkeyGesture(modifiers, vk, string.Join('+', segments));
        return true;
    }

    private static bool TryMapVirtualKey(string token, out uint virtualKey, out string canonical)
    {
        virtualKey = 0;
        canonical = string.Empty;
        var upper = token.ToUpperInvariant();

        if (upper.Length == 1)
        {
            var ch = upper[0];
            if (ch is >= 'A' and <= 'Z')
            {
                virtualKey = ch;
                canonical = upper;
                return true;
            }

            if (ch is >= '0' and <= '9')
            {
                virtualKey = ch;
                canonical = upper;
                return true;
            }
        }

        if (upper.StartsWith('F') && int.TryParse(upper[1..], out var fn) && fn is >= 1 and <= 12)
        {
            virtualKey = (uint)(0x70 + (fn - 1));
            canonical = $"F{fn}";
            return true;
        }

        switch (upper)
        {
            case "SPACE": virtualKey = 0x20; canonical = "Space"; return true;
            case "PAUSE": virtualKey = 0x13; canonical = "Pause"; return true;
            case "ESC":
            case "ESCAPE": virtualKey = 0x1B; canonical = "Escape"; return true;
            case "HOME": virtualKey = 0x24; canonical = "Home"; return true;
            case "END": virtualKey = 0x23; canonical = "End"; return true;
            case "INS":
            case "INSERT": virtualKey = 0x2D; canonical = "Insert"; return true;
            case "DEL":
            case "DELETE": virtualKey = 0x2E; canonical = "Delete"; return true;
            default: return false;
        }
    }
}

public sealed class GlobalHotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private readonly Dictionary<int, Action> _handlers = new();
    private HwndSource? _source;
    private IntPtr _hwnd;
    private int _nextId = 9000;
    private bool _disposed;

    public void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (_source is not null)
        {
            return;
        }

        var helper = new WindowInteropHelper(window);
        _hwnd = helper.Handle;
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);
    }

    public IReadOnlyList<string> RegisterSet(
        string? pauseGesture,
        Action onPauseToggle,
        string? stopGesture,
        Action onStop,
        string? emergencyStopGesture,
        Action onEmergencyStop)
    {
        UnregisterAll();
        var warnings = new List<string>();
        if (_hwnd == IntPtr.Zero)
        {
            return warnings;
        }

        TryRegister("Pause/Resume", pauseGesture, onPauseToggle, warnings);
        TryRegister("Stop", stopGesture, onStop, warnings);
        TryRegister("Emergency stop", emergencyStopGesture, onEmergencyStop, warnings);
        return warnings;
    }

    public void UnregisterAll()
    {
        if (_hwnd != IntPtr.Zero)
        {
            foreach (var id in _handlers.Keys)
            {
                _ = UnregisterHotKey(_hwnd, id);
            }
        }

        _handlers.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        UnregisterAll();
        _source?.RemoveHook(WndProc);
        _source = null;
        _hwnd = IntPtr.Zero;
    }

    private void TryRegister(string label, string? gestureText, Action handler, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(gestureText))
        {
            return;
        }

        if (!HotkeyGesture.TryParse(gestureText, out var gesture, out var error) || gesture is null)
        {
            warnings.Add($"{label}: {error}");
            return;
        }

        var id = ++_nextId;
        var modifiers = (uint)(gesture.Modifiers | HotkeyModifiers.NoRepeat);
        if (RegisterHotKey(_hwnd, id, modifiers, gesture.VirtualKey))
        {
            _handlers[id] = handler;
        }
        else
        {
            warnings.Add($"{label} ({gesture.CanonicalText}) could not be registered — another app may already use it.");
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && _handlers.TryGetValue(wParam.ToInt32(), out var action))
        {
            handled = true;
            action();
        }

        return IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
