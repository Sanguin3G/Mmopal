using Mmopal.Contracts;

namespace Mmopal.Native;

/// <summary>
/// Searches a saved BGRA32 frame. The score is 1 minus the mean absolute blue/green/red
/// difference divided by 255. Alpha is ignored. This is not an OpenCV matchTemplate result.
/// </summary>
public static class FrameVision
{
    public const string PixelFormatBgra32 = "Bgra32";

    public static int CountPixels(
        AutomationFrame frame,
        CaptureRegion? region,
        IReadOnlyList<int> lowerBoundBgr,
        IReadOnlyList<int> upperBoundBgr)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(lowerBoundBgr);
        ArgumentNullException.ThrowIfNull(upperBoundBgr);
        if (lowerBoundBgr.Count != 3 || upperBoundBgr.Count != 3)
        {
            throw new ArgumentException("BGR bounds must each contain exactly three values.");
        }

        var layout = Layout(frame);
        if (region is null)
        {
            return Count(layout, 0, 0, layout.Width, layout.Height, lowerBoundBgr, upperBoundBgr);
        }

        if (region.Width <= 0 || region.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(region), "The pixel region must have a positive size.");
        }

        if (!TryOverlap(frame.Region, region, out var localX, out var localY, out var width, out var height))
        {
            return 0;
        }

        return Count(layout, localX, localY, width, height, lowerBoundBgr, upperBoundBgr);
    }

    public static TemplateMatch? FindTemplate(
        AutomationFrame frame,
        ReadOnlySpan<byte> templatePixels,
        int templateWidth,
        int templateHeight,
        int templateStride,
        CaptureRegion? searchRegion,
        decimal minimumScore,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (templateWidth <= 0 || templateHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(templateWidth), "The template dimensions must be positive.");
        }

        if (minimumScore is < 0m or > 1m)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumScore), "The match score must be from 0 through 1.");
        }

        var layout = Layout(frame);
        var stride = templateStride == 0 ? checked(templateWidth * 4) : templateStride;
        if (stride < templateWidth * 4)
        {
            throw new ArgumentException("The template stride is shorter than one Bgra32 row.", nameof(templateStride));
        }

        var required = (long)stride * templateHeight;
        if (templatePixels.Length < required)
        {
            throw new ArgumentException("The template buffer is shorter than its stride and height.", nameof(templatePixels));
        }

        if (!TrySearchOrigin(frame, searchRegion, templateWidth, templateHeight, out var originX, out var originY, out var lastX, out var lastY))
        {
            return null;
        }

        decimal? best = null;
        var bestX = 0;
        var bestY = 0;
        for (var y = originY; y <= lastY; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = originX; x <= lastX; x++)
            {
                var score = Score(DifferenceSum(layout, templatePixels, stride, x, y, templateWidth, templateHeight), templateWidth * templateHeight);
                if (best is null || score > best.Value)
                {
                    best = score;
                    bestX = x;
                    bestY = y;
                }
            }
        }

        if (best is null || best.Value < minimumScore) return null;
        return new TemplateMatch(
            frame.Region.X + bestX,
            frame.Region.Y + bestY,
            templateWidth,
            templateHeight,
            best.Value);
    }

    private static int Count(
        FrameLayout layout,
        int localX,
        int localY,
        int width,
        int height,
        IReadOnlyList<int> lower,
        IReadOnlyList<int> upper)
    {
        var count = 0;
        for (var y = localY; y < localY + height; y++)
        {
            var row = y * layout.Stride + localX * 4;
            for (var x = 0; x < width; x++)
            {
                var index = row + x * 4;
                var blue = layout.Pixels[index];
                var green = layout.Pixels[index + 1];
                var red = layout.Pixels[index + 2];
                if (blue >= lower[0] && blue <= upper[0] &&
                    green >= lower[1] && green <= upper[1] &&
                    red >= lower[2] && red <= upper[2])
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static long DifferenceSum(
        FrameLayout layout,
        ReadOnlySpan<byte> template,
        int templateStride,
        int originX,
        int originY,
        int templateWidth,
        int templateHeight)
    {
        long sum = 0;
        for (var y = 0; y < templateHeight; y++)
        {
            var frameIndex = (originY + y) * layout.Stride + originX * 4;
            var templateIndex = y * templateStride;
            for (var x = 0; x < templateWidth; x++)
            {
                sum += Math.Abs(layout.Pixels[frameIndex] - template[templateIndex]);
                sum += Math.Abs(layout.Pixels[frameIndex + 1] - template[templateIndex + 1]);
                sum += Math.Abs(layout.Pixels[frameIndex + 2] - template[templateIndex + 2]);
                frameIndex += 4;
                templateIndex += 4;
            }
        }

        return sum;
    }

    private static decimal Score(long differenceSum, int pixelCount) =>
        1m - (decimal)differenceSum / (3m * pixelCount * 255m);

    private static bool TrySearchOrigin(
        AutomationFrame frame,
        CaptureRegion? searchRegion,
        int templateWidth,
        int templateHeight,
        out int originX,
        out int originY,
        out int lastX,
        out int lastY)
    {
        originX = 0;
        originY = 0;
        lastX = 0;
        lastY = 0;
        var search = searchRegion ?? frame.Region;
        if (search.Width <= 0 || search.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(searchRegion), "The search region must have a positive size.");
        }

        if (!TryOverlap(frame.Region, search, out var localX, out var localY, out var width, out var height))
        {
            return false;
        }

        if (width < templateWidth || height < templateHeight) return false;
        originX = localX;
        originY = localY;
        lastX = localX + width - templateWidth;
        lastY = localY + height - templateHeight;
        return true;
    }

    private static bool TryOverlap(
        CaptureRegion frame,
        CaptureRegion region,
        out int localX,
        out int localY,
        out int width,
        out int height)
    {
        var left = Math.Max(frame.X, region.X);
        var top = Math.Max(frame.Y, region.Y);
        var right = Math.Min(frame.X + frame.Width, region.X + region.Width);
        var bottom = Math.Min(frame.Y + frame.Height, region.Y + region.Height);
        localX = left - frame.X;
        localY = top - frame.Y;
        width = right - left;
        height = bottom - top;
        return width > 0 && height > 0;
    }

    private static FrameLayout Layout(AutomationFrame frame)
    {
        if (!string.Equals(frame.PixelFormat, PixelFormatBgra32, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Only Bgra32 frames can be searched.", nameof(frame));
        }

        if (frame.Region.Width <= 0 || frame.Region.Height <= 0)
        {
            throw new ArgumentException("The frame dimensions must be positive.", nameof(frame));
        }

        var stride = frame.Stride == 0 ? checked(frame.Region.Width * 4) : frame.Stride;
        if (stride < frame.Region.Width * 4)
        {
            throw new ArgumentException("The frame stride is shorter than one Bgra32 row.", nameof(frame));
        }

        var required = (long)stride * frame.Region.Height;
        if (frame.Pixels is null || frame.Pixels.LongLength < required)
        {
            throw new ArgumentException("The frame buffer is shorter than its stride and height.", nameof(frame));
        }

        return new FrameLayout(frame.Pixels, frame.Region.Width, frame.Region.Height, stride);
    }

    internal static void EnsureSearchable(AutomationFrame frame) => _ = Layout(frame);

    /// <summary>A part of the frame as a frame of its own, or null when it lies outside.</summary>
    public static AutomationFrame? Crop(AutomationFrame frame, CaptureRegion region) =>
        frame.Pixels.Length > 0 && CopyBgra(frame, region, out var width, out var height) is { } pixels
            ? new AutomationFrame(frame.CapturedAt, region with { Width = width, Height = height }, pixels, "Bgra32", width * 4)
            : null;

    internal static byte[]? CopyBgra(AutomationFrame frame, CaptureRegion region, out int width, out int height)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(region);
        var layout = Layout(frame);
        if (region.Width <= 0 || region.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(region), "The pixel region must have a positive size.");
        }

        if (!TryOverlap(frame.Region, region, out var localX, out var localY, out width, out height))
        {
            return null;
        }

        var packed = new byte[checked(width * height * 4)];
        var rowBytes = width * 4;
        for (var y = 0; y < height; y++)
        {
            Buffer.BlockCopy(layout.Pixels, (localY + y) * layout.Stride + localX * 4, packed, y * rowBytes, rowBytes);
        }

        return packed;
    }

    internal static bool TryTemplateWindow(
        AutomationFrame frame,
        CaptureRegion? searchRegion,
        int templateWidth,
        int templateHeight,
        out int originX,
        out int originY,
        out int width,
        out int height)
    {
        if (!TrySearchOrigin(frame, searchRegion, templateWidth, templateHeight, out originX, out originY, out var lastX, out var lastY))
        {
            width = 0;
            height = 0;
            return false;
        }

        width = lastX - originX + templateWidth;
        height = lastY - originY + templateHeight;
        return true;
    }

    /// <summary>
    /// Builds an OCR-friendly copy of <paramref name="region"/>: each pixel's brightest channel, contrast-stretched
    /// between the region's median and 99th percentile, inverted to dark text on light, and upscaled by
    /// <paramref name="scale"/>. Rarity-colored GW2 item names (purple legendaries in particular) are not read by
    /// Windows OCR from the raw frame but are after this. The returned frame's region starts at (0, 0); divide OCR
    /// coordinates by <paramref name="scale"/> and add the region origin to map them back.
    /// </summary>
    public static AutomationFrame? EnhanceTextContrast(AutomationFrame frame, CaptureRegion region, int scale = 2)
    {
        var pixels = CopyBgra(frame, region, out var width, out var height);
        if (pixels is null || width <= 0 || height <= 0)
        {
            return null;
        }

        scale = Math.Clamp(scale, 1, 4);
        var brightness = new byte[width * height];
        var histogram = new int[256];
        for (var i = 0; i < brightness.Length; i++)
        {
            var p = i * 4;
            var v = Math.Max(pixels[p], Math.Max(pixels[p + 1], pixels[p + 2]));
            brightness[i] = v;
            histogram[v]++;
        }

        int Percentile(double fraction)
        {
            var target = (int)(brightness.Length * fraction);
            var seen = 0;
            for (var v = 0; v < 256; v++)
            {
                seen += histogram[v];
                if (seen > target) return v;
            }

            return 255;
        }

        var low = Percentile(0.50);
        var high = Math.Max(low + 1, Percentile(0.99));
        var outWidth = width * scale;
        var outHeight = height * scale;
        var output = new byte[outWidth * outHeight * 4];
        for (var y = 0; y < outHeight; y++)
        {
            var row = (y / scale) * width;
            for (var x = 0; x < outWidth; x++)
            {
                var stretched = Math.Clamp((brightness[row + x / scale] - low) * 255 / (high - low), 0, 255);
                var value = (byte)(255 - stretched);
                var o = (y * outWidth + x) * 4;
                output[o] = value;
                output[o + 1] = value;
                output[o + 2] = value;
                output[o + 3] = 255;
            }
        }

        return new AutomationFrame(frame.CapturedAt, new CaptureRegion(0, 0, outWidth, outHeight), output, PixelFormatBgra32, outWidth * 4);
    }

    private readonly record struct FrameLayout(byte[] Pixels, int Width, int Height, int Stride);
}
