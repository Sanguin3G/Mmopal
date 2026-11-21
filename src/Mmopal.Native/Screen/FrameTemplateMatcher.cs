using System.Runtime.InteropServices;
using Mmopal.Contracts;
using OpenCvSharp;

namespace Mmopal.Native;

/// <summary>
/// Searches pixels inside the supplied frame with OpenCV. It does not capture the desktop.
/// Textured templates use normalized correlation.
/// A flat template has no correlation variance, so that case uses normalized squared difference.
/// </summary>
public sealed class FrameTemplateMatcher : ITemplateMatcher
{
    public Task<TemplateMatch?> FindAsync(
        AutomationFrame frame,
        TemplateDefinition template,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(template);
        cancellationToken.ThrowIfCancellationRequested();
        if (template.MatchScore is < 0m or > 1m)
        {
            throw new ArgumentOutOfRangeException(nameof(template), "The match score must be from 0 through 1.");
        }

        FrameVision.EnsureSearchable(frame);
        using var loaded = Cv2.ImRead(template.FilePath, ImreadModes.Unchanged);
        if (loaded.Empty())
        {
            throw new InvalidDataException($"OpenCV could not read the template image '{template.FilePath}'.");
        }

        using var templateBgr = ToBgr(loaded);
        if (!FrameVision.TryTemplateWindow(
                frame,
                template.Region,
                templateBgr.Width,
                templateBgr.Height,
                out var originX,
                out var originY,
                out var windowWidth,
                out var windowHeight))
        {
            return Task.FromResult<TemplateMatch?>(null);
        }

        var handle = GCHandle.Alloc(frame.Pixels, GCHandleType.Pinned);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stride = frame.Stride == 0 ? frame.Region.Width * 4 : frame.Stride;
            using var bgra = Mat.FromPixelData(
                frame.Region.Height,
                frame.Region.Width,
                MatType.CV_8UC4,
                handle.AddrOfPinnedObject(),
                stride);
            using var bgr = new Mat();
            Cv2.CvtColor(bgra, bgr, ColorConversionCodes.BGRA2BGR);
            using var window = new Mat(bgr, new Rect(originX, originY, windowWidth, windowHeight));
            if (!TryScore(window, templateBgr, out var score, out var localX, out var localY) ||
                (decimal)score < template.MatchScore)
            {
                return Task.FromResult<TemplateMatch?>(null);
            }

            return Task.FromResult<TemplateMatch?>(new TemplateMatch(
                frame.Region.X + originX + localX,
                frame.Region.Y + originY + localY,
                templateBgr.Width,
                templateBgr.Height,
                (decimal)score));
        }
        finally
        {
            handle.Free();
        }
    }

    private static Mat ToBgr(Mat source)
    {
        var converted = new Mat();
        switch (source.Channels())
        {
            case 4:
                Cv2.CvtColor(source, converted, ColorConversionCodes.BGRA2BGR);
                return converted;
            case 3:
                source.CopyTo(converted);
                return converted;
            case 1:
                Cv2.CvtColor(source, converted, ColorConversionCodes.GRAY2BGR);
                return converted;
            default:
                converted.Dispose();
                throw new NotSupportedException("The template image must be gray, BGR, or BGRA.");
        }
    }

    private static bool TryScore(Mat image, Mat template, out double score, out int x, out int y)
    {
        if (IsFlat(template))
        {
            return TrySquaredDifference(image, template, out score, out x, out y);
        }

        using var correlation = new Mat();
        Cv2.MatchTemplate(image, template, correlation, TemplateMatchModes.CCoeffNormed);
        Cv2.MinMaxLoc(correlation, out _, out var correlationScore, out _, out var correlationAt);
        if (double.IsFinite(correlationScore))
        {
            score = correlationScore;
            x = correlationAt.X;
            y = correlationAt.Y;
            return true;
        }

        return TrySquaredDifference(image, template, out score, out x, out y);
    }

    private static bool IsFlat(Mat template)
    {
        Cv2.MeanStdDev(template, out _, out var deviation);
        return deviation.Val0 <= 1e-3 && deviation.Val1 <= 1e-3 && deviation.Val2 <= 1e-3;
    }

    private static bool TrySquaredDifference(Mat image, Mat template, out double score, out int x, out int y)
    {
        using var difference = new Mat();
        Cv2.MatchTemplate(image, template, difference, TemplateMatchModes.SqDiffNormed);
        Cv2.MinMaxLoc(difference, out var differenceScore, out _, out var differenceAt, out _);
        if (!double.IsFinite(differenceScore))
        {
            score = 0;
            x = 0;
            y = 0;
            return false;
        }

        score = 1d - differenceScore;
        x = differenceAt.X;
        y = differenceAt.Y;
        return true;
    }
}
