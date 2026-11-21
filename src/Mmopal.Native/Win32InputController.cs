using System.ComponentModel;
using System.Runtime.InteropServices;
using Mmopal.Contracts;

namespace Mmopal.Native;

/// <summary>
/// Sends a local click or key through Win32. The worker does not construct this type, and tests must not call it.
/// </summary>
public sealed partial class Win32InputController : IInputController
{
    private const int InputMouse = 0;
    private const int InputKeyboard = 1;
    private const uint MouseEventMove = 0x0001;
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private const uint MouseEventVirtualDesk = 0x4000;
    private const uint MouseEventAbsolute = 0x8000;
    private const uint KeyEventExtendedKey = 0x0001;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventUnicode = 0x0004;
    private const int VirtualScreenX = 76;
    private const int VirtualScreenY = 77;
    private const int VirtualScreenWidth = 78;
    private const int VirtualScreenHeight = 79;

    internal static int NativeInputStructBytes => Marshal.SizeOf<Input>();

    public async Task ClickAsync(int x, int y, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var originX = GetSystemMetrics(VirtualScreenX);
        var originY = GetSystemMetrics(VirtualScreenY);
        var width = GetSystemMetrics(VirtualScreenWidth);
        var height = GetSystemMetrics(VirtualScreenHeight);
        if (width <= 1 || height <= 1)
        {
            throw new InvalidOperationException("The virtual screen size is unavailable.");
        }

        var absoluteX = (int)Math.Clamp((long)(x - originX) * 65535L / (width - 1), 0, 65535);
        var absoluteY = (int)Math.Clamp((long)(y - originY) * 65535L / (height - 1), 0, 65535);
        const uint moveTo = MouseEventMove | MouseEventAbsolute | MouseEventVirtualDesk;
        // Move -> wait -> LeftDown -> wait -> LeftUp so DirectX/Coherent UI registers hover and click cleanly.
        SendOrThrow([Mouse(absoluteX, absoluteY, moveTo)]);
        await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        SendOrThrow([Mouse(absoluteX, absoluteY, moveTo | MouseEventLeftDown)]);
        await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        SendOrThrow([Mouse(absoluteX, absoluteY, moveTo | MouseEventLeftUp)]);
    }

    public async Task KeyPressAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var plan = VirtualKeyNames.Plan(key);
        var downEvents = new List<Input>(plan.Count);
        var upEvents = new List<Input>(plan.Count);
        foreach (var item in plan)
        {
            var scan = (ushort)MapVirtualKeyW(item.VirtualKey, 0);
            var flags = IsExtendedVirtualKey(item.VirtualKey) ? KeyEventExtendedKey : 0u;
            if (item.KeyUp)
            {
                upEvents.Add(Keyboard(item.VirtualKey, scan, flags | KeyEventKeyUp));
            }
            else
            {
                downEvents.Add(Keyboard(item.VirtualKey, scan, flags));
            }
        }

        await SendSpacedAsync(downEvents, 25, cancellationToken).ConfigureAwait(false);
        if (upEvents.Count > 0)
        {
            await Task.Delay(45, cancellationToken).ConfigureAwait(false);
            await SendSpacedAsync(upEvents, 20, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>A single event is sent as is; a chord's events go one at a time, each followed by <paramref name="gapMs"/>.</summary>
    private static async Task SendSpacedAsync(List<Input> events, int gapMs, CancellationToken cancellationToken)
    {
        if (events.Count == 1)
        {
            SendOrThrow([events[0]]);
            return;
        }

        foreach (var keyEvent in events)
        {
            SendOrThrow([keyEvent]);
            await Task.Delay(gapMs, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsExtendedVirtualKey(ushort vk) =>
        vk is >= 0x21 and <= 0x2E;

    public async Task SendTextAsync(string text, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(text)) return;
        for (var i = 0; i < text.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SendOrThrow([
                Keyboard(0, text[i], KeyEventUnicode),
                Keyboard(0, text[i], KeyEventUnicode | KeyEventKeyUp)
            ]);
            if (i + 1 < text.Length)
            {
                await Task.Delay(6, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    private static Input Mouse(int x, int y, uint flags) => new()
    {
        Type = InputMouse,
        Data = new InputUnion
        {
            Mouse = new MouseInput
            {
                X = x,
                Y = y,
                Flags = flags
            }
        }
    };

    private static Input Keyboard(ushort virtualKey, ushort scan, uint flags) => new()
    {
        Type = InputKeyboard,
        Data = new InputUnion
        {
            Keyboard = new KeyboardInput
            {
                VirtualKey = virtualKey,
                Scan = scan,
                Flags = flags
            }
        }
    };

    private static void SendOrThrow(Input[] inputs)
    {
        var sent = SendInput((uint)inputs.Length, inputs, NativeInputStructBytes);
        if (sent != inputs.Length)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "SendInput did not deliver every event.");
        }
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint SendInput(uint count, Input[] inputs, int size);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);

    [LibraryImport("user32.dll")]
    private static partial uint MapVirtualKeyW(uint uCode, uint uMapType);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }
}
