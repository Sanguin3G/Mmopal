using System.Buffers.Binary;
using Mmopal.Contracts;
using Mmopal.Native;

namespace Mmopal.Domain.Tests;

public sealed class FrameVisionTests
{
    [Fact]
    public void Count_pixels_uses_inclusive_bgr_bounds_and_ignores_alpha()
    {
        var frame = Frame(0, 0, 2, 1, [10, 20, 30, 255, 10, 20, 31, 0]);

        var count = FrameVision.CountPixels(frame, null, [10, 20, 30], [10, 20, 30]);

        Assert.Equal(1, count);
    }

    [Fact]
    public void Count_pixels_keeps_only_the_requested_screen_region()
    {
        var frame = Frame(100, 200, 2, 1, [1, 2, 3, 4, 9, 8, 7, 6]);

        var count = FrameVision.CountPixels(frame, new CaptureRegion(101, 200, 1, 1), [9, 8, 7], [9, 8, 7]);

        Assert.Equal(1, count);
    }

    [Fact]
    public void Count_pixels_returns_zero_when_the_region_misses_the_frame()
    {
        var frame = Frame(0, 0, 1, 1, [1, 2, 3, 4]);

        var count = FrameVision.CountPixels(frame, new CaptureRegion(10, 10, 2, 2), [0, 0, 0], [255, 255, 255]);

        Assert.Equal(0, count);
    }

    [Fact]
    public void Count_pixels_rejects_a_bound_that_is_not_three_values()
    {
        var frame = Frame(0, 0, 1, 1, [1, 2, 3, 4]);

        var error = Assert.Throws<ArgumentException>(() => FrameVision.CountPixels(frame, null, [1, 2], [1, 2, 3]));

        Assert.Contains("three", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Template_search_reports_the_screen_position_and_measured_score()
    {
        var frame = Frame(5, 7, 3, 1, [0, 0, 0, 255, 9, 8, 7, 255, 1, 1, 1, 255]);

        var match = FrameVision.FindTemplate(frame, [9, 8, 7, 255], 1, 1, 4, null, 0.8m);

        Assert.NotNull(match);
        Assert.Equal(6, match.X);
        Assert.Equal(7, match.Y);
        Assert.Equal(1, match.Width);
        Assert.Equal(1, match.Height);
        Assert.Equal(1m, match.Score);
    }

    [Fact]
    public void Template_search_uses_the_mean_absolute_bgr_score()
    {
        var frame = Frame(0, 0, 1, 1, [0, 0, 0, 255]);

        var match = FrameVision.FindTemplate(frame, [255, 0, 0, 0], 1, 1, 4, null, 0m);

        Assert.NotNull(match);
        Assert.Equal(1m - (1m / 3m), match.Score);
    }

    [Fact]
    public void Template_search_returns_null_below_the_threshold()
    {
        var frame = Frame(0, 0, 1, 1, [0, 0, 0, 255]);

        var match = FrameVision.FindTemplate(frame, [255, 0, 0, 255], 1, 1, 4, null, 0.99m);

        Assert.Null(match);
    }

    [Fact]
    public void Template_search_keeps_the_first_equal_match()
    {
        var frame = Frame(0, 0, 2, 1, [4, 5, 6, 1, 4, 5, 6, 9]);

        var match = FrameVision.FindTemplate(frame, [4, 5, 6, 0], 1, 1, 4, null, 1m);

        Assert.NotNull(match);
        Assert.Equal(0, match.X);
        Assert.Equal(0, match.Y);
    }

    [Fact]
    public async Task Bmp_template_is_found_inside_the_frame()
    {
        var path = Path.Combine(Path.GetTempPath(), "mmopal-template-" + Guid.NewGuid().ToString("N") + ".bmp");
        try
        {
            File.WriteAllBytes(path, BottomUp24BitBmp(9, 8, 7));
            var frame = Frame(20, 30, 2, 1, [0, 0, 0, 255, 9, 8, 7, 255]);
            var matcher = new FrameTemplateMatcher();

            var match = await matcher.FindAsync(frame, new TemplateDefinition("spot", path, MatchScore: 1m));

            Assert.NotNull(match);
            Assert.Equal(21, match.X);
            Assert.Equal(30, match.Y);
            Assert.Equal(1m, match.Score);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task OpenCv_template_search_keeps_a_textured_match_and_honors_the_region()
    {
        var path = Path.Combine(Path.GetTempPath(), "mmopal-template-" + Guid.NewGuid().ToString("N") + ".bmp");
        try
        {
            File.WriteAllBytes(path, BottomUp24BitBmp(
                2,
                2,
                [9, 8, 7, 1, 2, 3, 4, 5, 6, 9, 9, 9]));
            var frame = Frame(20, 30, 3, 2,
            [
                0, 0, 0, 255, 9, 8, 7, 255, 1, 2, 3, 255,
                0, 0, 0, 255, 4, 5, 6, 255, 9, 9, 9, 255
            ]);
            var matcher = new FrameTemplateMatcher();

            var match = await matcher.FindAsync(frame, new TemplateDefinition("spot", path, MatchScore: 0.99m));
            var outside = await matcher.FindAsync(
                frame,
                new TemplateDefinition("spot", path, new CaptureRegion(20, 30, 2, 2), MatchScore: 0.99m));

            Assert.NotNull(match);
            Assert.Equal(21, match.X);
            Assert.Equal(30, match.Y);
            Assert.True(match.Score >= 0.99m);
            Assert.Null(outside);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Key_plan_presses_modifiers_before_the_key_and_releases_in_reverse()
    {
        var plan = VirtualKeyNames.Plan("ctrl+shift+o");

        Assert.Equal(
            new[]
            {
                new KeyEventPlan(0x11, false),
                new KeyEventPlan(0x10, false),
                new KeyEventPlan(0x4F, false),
                new KeyEventPlan(0x4F, true),
                new KeyEventPlan(0x10, true),
                new KeyEventPlan(0x11, true)
            },
            plan);
    }

    [Fact]
    public void Win32_input_struct_matches_the_process_width()
    {
        Assert.Equal(Environment.Is64BitProcess ? 40 : 28, Win32InputController.NativeInputStructBytes);
    }

    [Fact]
    public void Windows_desktop_screenshot_provider_reads_exact_bgra32_pixels_from_gdi_dc()
    {
        var sourceDc = CreateCompatibleDC(IntPtr.Zero);
        Assert.NotEqual(IntPtr.Zero, sourceDc);
        var info = new TestBitmapInfo
        {
            Size = 40,
            Width = 2,
            Height = -2,
            Planes = 1,
            BitCount = 32,
            Compression = 0
        };
        var dib = CreateDIBSection(sourceDc, ref info, 0, out var bitsPtr, IntPtr.Zero, 0);
        Assert.NotEqual(IntPtr.Zero, dib);
        var previous = SelectObject(sourceDc, dib);
        try
        {
            var expected = new byte[]
            {
                10, 20, 30, 0,  40, 50, 60, 0,
                70, 80, 90, 0,  11, 22, 33, 0
            };
            System.Runtime.InteropServices.Marshal.Copy(expected, 0, bitsPtr, expected.Length);

            var frame = WindowsDesktopScreenshotProvider.CaptureFromDeviceContext(sourceDc, new CaptureRegion(0, 0, 2, 2));

            Assert.Equal("Bgra32", frame.PixelFormat);
            Assert.Equal(8, frame.Stride);
            Assert.Equal(16, frame.Pixels.Length);
            Assert.Equal(10, frame.Pixels[0]);
            Assert.Equal(20, frame.Pixels[1]);
            Assert.Equal(30, frame.Pixels[2]);
            Assert.Equal(11, frame.Pixels[12]);
            Assert.Equal(22, frame.Pixels[13]);
            Assert.Equal(33, frame.Pixels[14]);
        }
        finally
        {
            SelectObject(sourceDc, previous);
            DeleteObject(dib);
            DeleteDC(sourceDc);
        }
    }

    [Fact]
    public async Task Windows_desktop_screenshot_provider_captures_desktop_or_reports_unavailable_desktop()
    {
        var provider = new WindowsDesktopScreenshotProvider();
        var region = new CaptureRegion(0, 0, 4, 4);

        try
        {
            var frame = await provider.CaptureAsync(region);
            Assert.Equal(region, frame.Region);
            Assert.Equal("Bgra32", frame.PixelFormat);
            Assert.Equal(16, frame.Stride);
            Assert.Equal(64, frame.Pixels.Length);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode is 5 or 6)
        {
            // Non-interactive window station or locked desktop prevents BitBlt from the screen DC,
            // while confirming all GDI32 P/Invoke entry points resolved cleanly.
        }
    }

    [System.Runtime.InteropServices.DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [System.Runtime.InteropServices.DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateDIBSection(IntPtr dc, ref TestBitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [System.Runtime.InteropServices.DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

    [System.Runtime.InteropServices.DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteObject(IntPtr obj);

    [System.Runtime.InteropServices.DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteDC(IntPtr dc);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct TestBitmapInfo
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
        public uint Color;
    }

    private static AutomationFrame Frame(int x, int y, int width, int height, byte[] pixels) =>
        new(DateTimeOffset.UtcNow, new CaptureRegion(x, y, width, height), pixels);

    private static byte[] BottomUp24BitBmp(byte blue, byte green, byte red) =>
        BottomUp24BitBmp(1, 1, [blue, green, red]);

    private static byte[] BottomUp24BitBmp(int width, int height, byte[] bgr)
    {
        var rowStride = (width * 3 + 3) & ~3;
        var pixels = rowStride * height;
        var bytes = new byte[54 + pixels];
        bytes[0] = (byte)'B';
        bytes[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(2, 4), bytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(10, 4), 54);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(14, 4), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18, 4), width);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(22, 4), height);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(26, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(28, 2), 24);
        for (var y = 0; y < height; y++)
        {
            var sourceRow = height - 1 - y;
            for (var x = 0; x < width; x++)
            {
                var source = (sourceRow * width + x) * 3;
                var target = 54 + y * rowStride + x * 3;
                bytes[target] = bgr[source];
                bytes[target + 1] = bgr[source + 1];
                bytes[target + 2] = bgr[source + 2];
            }
        }

        return bytes;
    }
}
