using System.ComponentModel;
using System.Runtime.InteropServices;
using Mmopal.Contracts;

namespace Mmopal.Native;

/// <summary>
/// Captures a screen region without introducing a third-party imaging dependency.
/// The returned bytes are top-down BGRA32 pixels.
/// </summary>
public sealed class WindowsDesktopScreenshotProvider : IScreenshotProvider
{
    private const int Srccopy = 0x00CC0020;
    private const int DibRgbColors = 0;
    private const int BiRgb = 0;

    public Task<AutomationFrame> CaptureAsync(
        CaptureRegion region,
        CancellationToken cancellationToken = default)
    {
        ValidateRegion(region);
        return Task.Run(() => CaptureCore(region, cancellationToken), cancellationToken);
    }

    private static AutomationFrame CaptureCore(CaptureRegion region, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) throw Win32Failure("Could not acquire the desktop device context.");

        try
        {
            return CaptureFromDeviceContext(screenDc, region, cancellationToken);
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    internal static AutomationFrame CaptureFromDeviceContext(
        IntPtr sourceDc,
        CaptureRegion region,
        CancellationToken cancellationToken = default)
    {
        ValidateRegion(region);
        cancellationToken.ThrowIfCancellationRequested();

        var memoryDc = IntPtr.Zero;
        var bitmap = IntPtr.Zero;
        var previousObject = IntPtr.Zero;
        try
        {
            memoryDc = CreateCompatibleDC(sourceDc);
            if (memoryDc == IntPtr.Zero)
            {
                throw Win32Failure("Could not create a compatible device context.");
            }

            bitmap = CreateCompatibleBitmap(sourceDc, region.Width, region.Height);
            if (bitmap == IntPtr.Zero)
            {
                throw Win32Failure("Could not create a compatible capture bitmap.");
            }

            previousObject = SelectObject(memoryDc, bitmap);
            if (previousObject == IntPtr.Zero)
            {
                throw Win32Failure("Could not select the capture bitmap.");
            }

            if (!BitBlt(memoryDc, 0, 0, region.Width, region.Height, sourceDc, region.X, region.Y, Srccopy))
            {
                throw Win32Failure("Could not copy the desktop region.");
            }

            // GDI requires the bitmap to be deselected from the memory DC before calling GetDIBits.
            SelectObject(memoryDc, previousObject);
            previousObject = IntPtr.Zero;

            var info = new BitmapInfo
            {
                Header = new BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                    Width = region.Width,
                    Height = -region.Height,
                    Planes = 1,
                    BitCount = 32,
                    Compression = BiRgb
                }
            };
            var stride = checked(region.Width * 4);
            var pixels = new byte[checked(stride * region.Height)];
            if (GetDIBits(memoryDc, bitmap, 0, (uint)region.Height, pixels, ref info, DibRgbColors) == 0)
            {
                throw Win32Failure("Could not read the captured desktop region.");
            }

            return new AutomationFrame(DateTimeOffset.UtcNow, region, pixels, "Bgra32", stride);
        }
        finally
        {
            if (previousObject != IntPtr.Zero && memoryDc != IntPtr.Zero) SelectObject(memoryDc, previousObject);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memoryDc != IntPtr.Zero) DeleteDC(memoryDc);
        }
    }

    private static void ValidateRegion(CaptureRegion region)
    {
        if (region.Width <= 0 || region.Height <= 0) throw new ArgumentOutOfRangeException(nameof(region), "Capture dimensions must be positive.");
        if (region.Width > 16_384 || region.Height > 16_384) throw new ArgumentOutOfRangeException(nameof(region), "Capture dimensions are unreasonably large.");
    }

    private static Win32Exception Win32Failure(string message)
    {
        var error = Marshal.GetLastWin32Error();
        return new Win32Exception(error, $"{message} (Win32 error {error}).");
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr item);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool BitBlt(IntPtr destination, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, int rop);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint startScan, uint scanLines, [Out] byte[] bits, ref BitmapInfo bitmapInfo, uint usage);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteObject(IntPtr item);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteDC(IntPtr dc);

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public RgbQuad Colors;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RgbQuad
    {
        public byte Blue;
        public byte Green;
        public byte Red;
        public byte Reserved;
    }
}
