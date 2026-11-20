using System.Runtime.InteropServices;
using Mmopal.Contracts;
using OpenCvSharp;

namespace Mmopal.Native;

/// <summary>Writes a captured BGRA32 frame to disk, for calibration samples when a screen read fails.</summary>
public static class FrameImageFile
{
    public static bool TrySavePng(AutomationFrame frame, string path)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Pixels.Length == 0 || frame.Region.Width <= 0 || frame.Region.Height <= 0)
        {
            return false;
        }

        var stride = frame.Stride == 0 ? frame.Region.Width * 4 : frame.Stride;
        if (frame.Pixels.Length < stride * frame.Region.Height)
        {
            return false;
        }

        var handle = GCHandle.Alloc(frame.Pixels, GCHandleType.Pinned);
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using var bgra = Mat.FromPixelData(frame.Region.Height, frame.Region.Width, MatType.CV_8UC4, handle.AddrOfPinnedObject(), stride);
            return Cv2.ImWrite(path, bgra);
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            handle.Free();
        }
    }
}
