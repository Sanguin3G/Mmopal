using System.Runtime.InteropServices;

namespace Mmopal.Native;

public sealed record DisplayScaleInfo(
    int Dpi,
    int ScalePercent,
    bool IsRecommendedScale,
    string Summary);

public static partial class WindowsDisplayHealth
{
    public static DisplayScaleInfo GetPrimaryDisplayScale()
    {
        var dpi = 96;
        try
        {
            var reported = (int)GetDpiForSystem();
            if (reported >= 96)
            {
                dpi = reported;
            }
        }
        catch
        {
            // Keep the 96 DPI default when the system DPI cannot be queried.
        }

        var scalePercent = (int)Math.Round(dpi * 100.0 / 96.0);
        var isRecommended = scalePercent == 100;
        var summary = isRecommended
            ? $"100% ({dpi} DPI) — Matches 1024x768 template calibration"
            : $"{scalePercent}% ({dpi} DPI) — Set Windows Display Scale to 100% for accurate template matching";
        return new DisplayScaleInfo(dpi, scalePercent, isRecommended, summary);
    }

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForSystem();
}
