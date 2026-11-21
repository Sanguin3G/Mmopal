using Microsoft.Extensions.Logging;
using Mmopal.Contracts;
using Mmopal.Gw2;
using Mmopal.Native;
using Mmopal.Worker.Vision;

namespace Mmopal.Worker;

/// <summary>Launcher screen analysis by pixel colour: the white login card, the PLAY button and their themes.</summary>
public sealed partial class Gw2TradingPostUiDriver
{
    internal static LauncherWhiteCardState? DetectLauncherWhiteCard(AutomationFrame frame)
    {
        if (frame.Pixels.Length == 0 || frame.Region.Width < 500 || frame.Region.Height < 500)
        {
            return null;
        }

        var width = frame.Region.Width;
        var height = frame.Region.Height;
        var stride = frame.Stride > 0 ? frame.Stride : width * 4;
        if (frame.Pixels.Length < stride * height)
        {
            return null;
        }

        var whiteSamples = 0;
        var totalSamples = 0;
        var leftSampleLimit = Math.Min(460, width);
        var bottomSampleLimit = Math.Min(720, height);
        for (var y = 180; y < bottomSampleLimit; y += 4)
        {
            for (var x = 20; x < leftSampleLimit; x += 4)
            {
                totalSamples++;
                if (IsCardWhite(frame.Pixels, stride, x, y))
                {
                    whiteSamples++;
                }
            }
        }

        if (totalSamples == 0 || whiteSamples * 100 / totalSamples < 35)
        {
            return null;
        }

        // Measure vertical bounds along the clean left card margin so bright right-side theme artwork
        // (such as EoD Jade Mech highlights) cannot shift cardTop upward.
        var cardTop = int.MaxValue;
        var cardBottom = 0;
        var marginMaxX = Math.Min(44, width - 1);
        for (var y = 20; y < height; y += 2)
        {
            var marginWhite = 0;
            var marginTotal = 0;
            for (var x = 20; x <= marginMaxX; x += 4)
            {
                marginTotal++;
                if (IsCardWhite(frame.Pixels, stride, x, y))
                {
                    marginWhite++;
                }
            }

            if (marginTotal > 0 && marginWhite * 100 / marginTotal >= 60)
            {
                if (y < cardTop) cardTop = y;
                if (y > cardBottom) cardBottom = y;
            }
        }

        if (cardBottom - cardTop < 250)
        {
            cardTop = int.MaxValue;
            cardBottom = 0;
            var bandMaxX = Math.Min(420, width);
            for (var y = 100; y < Math.Min(height - 2, 755); y += 3)
            {
                for (var x = 10; x < bandMaxX; x += 5)
                {
                    if (IsCardWhite(frame.Pixels, stride, x, y))
                    {
                        if (y < cardTop) cardTop = y;
                        if (y > cardBottom) cardBottom = y;
                    }
                }
            }
        }

        var cardLeft = int.MaxValue;
        var cardRight = 0;
        var maxScanX = width - 4;
        for (var y = Math.Max(0, cardTop); y <= Math.Min(height - 2, cardBottom); y += 6)
        {
            for (var x = 10; x < maxScanX; x += 5)
            {
                if (IsCardWhite(frame.Pixels, stride, x, y))
                {
                    if (x < cardLeft) cardLeft = x;
                    if (x > cardRight) cardRight = x;
                }
            }
        }

        if (cardRight - cardLeft < 400 || cardBottom - cardTop < 250)
        {
            return null;
        }

        var rawCardW = cardRight - cardLeft;
        var rawCardH = cardBottom - cardTop;
        // Themes with dark bottom/right art (Path of Fire, Living World Season 4, Icebrood Saga) truncate
        // white-pixel bounds even though the HTML form stays anchored at fixed offsets from (cardLeft, cardTop).
        var cardW = cardLeft <= 40 && rawCardW < 960
            ? Math.Max(rawCardW, Math.Min(width - 20 - cardLeft, 965))
            : rawCardW;
        var cardH = cardTop is >= 140 and <= 230 && rawCardH < 530
            ? Math.Max(rawCardH, Math.Min(height - 20 - cardTop, 532))
            : rawCardH;
        var loginX = Math.Clamp(cardLeft + (int)Math.Round(cardW * 0.098), 0, width - 1);
        var loginY = Math.Clamp(cardTop + (int)Math.Round(cardH * 0.665), 0, height - 1);
        var playX = Math.Clamp(cardLeft + (int)Math.Round(cardW * 0.780), 0, width - 1);
        var playY = Math.Clamp(cardTop + (int)Math.Round(cardH * 0.858), 0, height - 1);

        var buttonColored = 0;
        var buttonTotal = 0;
        var btnMinX = Math.Max(0, loginX - 40);
        var btnMaxX = Math.Min(width - 1, loginX + 40);
        var btnMinY = Math.Max(0, loginY - 10);
        var btnMaxY = Math.Min(height - 1, loginY + 10);
        for (var y = btnMinY; y <= btnMaxY; y += 2)
        {
            for (var x = btnMinX; x <= btnMaxX; x += 4)
            {
                buttonTotal++;
                var (r, g, b) = SampleRgb(frame.Pixels, stride, x, y);
                if (Math.Min(r, Math.Min(g, b)) < 195)
                {
                    buttonColored++;
                }
            }
        }

        var surroundWhite = 0;
        var surroundTotal = 0;
        foreach (var sy in new[] { loginY - 26, loginY + 26 })
        {
            if (sy < 0 || sy >= height) continue;
            for (var x = btnMinX; x <= btnMaxX; x += 4)
            {
                surroundTotal++;
                var (r, g, b) = SampleRgb(frame.Pixels, stride, x, sy);
                if (IsLightCardPixel(r, g, b))
                {
                    surroundWhite++;
                }
            }
        }

        var loginActive = buttonTotal > 0 &&
                          buttonColored * 100 / buttonTotal >= 35 &&
                          (surroundTotal == 0 || surroundWhite * 100 / surroundTotal >= 55);

        if (!loginActive)
        {
            // Scan left login column vertically for a compact colored/dark LOG IN box bounded by light card pixels
            // when expansion art or unclipped launcher height shifts the button Y slightly.
            var colMinX = Math.Clamp(cardLeft + 30, 0, width - 1);
            var colMaxX = Math.Clamp(cardLeft + 215, colMinX + 1, width - 1);
            var colMinY = Math.Clamp(cardTop + (int)Math.Round(cardH * 0.50), 20, height - 20);
            var colMaxY = Math.Clamp(cardTop + (int)Math.Round(cardH * 0.78), colMinY, height - 20);
            var bandStart = -1;
            var bestBandStart = -1;
            var bestBandEnd = -1;
            var bestBandPixels = 0;

            for (var y = colMinY; y <= colMaxY; y += 2)
            {
                var rowDark = 0;
                var rowTotal = 0;
                for (var x = colMinX; x <= colMaxX; x += 4)
                {
                    rowTotal++;
                    var (r, g, b) = SampleRgb(frame.Pixels, stride, x, y);
                    if (Math.Min(r, Math.Min(g, b)) < 185)
                    {
                        rowDark++;
                    }
                }

                var isButtonRow = rowTotal > 0 && rowDark * 100 / rowTotal >= 45;
                if (isButtonRow)
                {
                    if (bandStart < 0) bandStart = y;
                    if (rowDark > bestBandPixels)
                    {
                        bestBandPixels = rowDark;
                        bestBandStart = bandStart;
                        bestBandEnd = y;
                    }
                }
                else
                {
                    if (bandStart >= 0 && bestBandStart == bandStart && y - 2 - bandStart >= 14 && y - 2 - bandStart <= 46)
                    {
                        bestBandEnd = y - 2;
                        break;
                    }

                    bandStart = -1;
                }
            }

            if (bestBandStart >= 0 && bestBandEnd - bestBandStart is >= 14 and <= 46)
            {
                var aboveY = Math.Max(0, bestBandStart - 10);
                var belowY = Math.Min(height - 1, bestBandEnd + 14);
                var lightAround = 0;
                var totalAround = 0;
                foreach (var sy in new[] { aboveY, belowY })
                {
                    for (var x = colMinX; x <= colMaxX; x += 6)
                    {
                        totalAround++;
                        var (r, g, b) = SampleRgb(frame.Pixels, stride, x, sy);
                        if (IsLightCardPixel(r, g, b))
                        {
                            lightAround++;
                        }
                    }
                }

                if (totalAround > 0 && lightAround * 100 / totalAround >= 55)
                {
                    var sumX = 0L;
                    var sumY = 0L;
                    var hits = 0;
                    for (var y = bestBandStart; y <= bestBandEnd; y += 2)
                    {
                        for (var x = colMinX; x <= colMaxX; x += 4)
                        {
                            var (r, g, b) = SampleRgb(frame.Pixels, stride, x, y);
                            if (Math.Min(r, Math.Min(g, b)) < 185)
                            {
                                sumX += x;
                                sumY += y;
                                hits++;
                            }
                        }
                    }

                    if (hits > 0)
                    {
                        loginX = (int)(sumX / hits);
                        loginY = (int)(sumY / hits);
                        loginActive = true;
                    }
                }
            }
        }

        var playColored = 0;
        var playTotal = 0;
        var playMinX = Math.Max(0, playX - 30);
        var playMaxX = Math.Min(width - 1, playX + 30);
        var playMinY = Math.Max(0, playY - 10);
        var playMaxY = Math.Min(height - 1, playY + 10);
        for (var y = playMinY; y <= playMaxY; y += 2)
        {
            for (var x = playMinX; x <= playMaxX; x += 5)
            {
                playTotal++;
                var (pr, pg, pb) = SampleRgb(frame.Pixels, stride, x, y);
                if (IsColoredPlayPixel(pr, pg, pb))
                {
                    playColored++;
                }
            }
        }

        var playActive = !loginActive && playTotal > 0 && playColored * 100 / playTotal >= 25;
        if (!loginActive && !playActive)
        {
            // Expand slightly around predicted PLAY coordinates to locate the real PLAY button centroid across launcher sizes.
            var searchMinX = Math.Max(620, playX - 90);
            var searchMaxX = Math.Min(width - 10, playX + 110);
            var searchMinY = Math.Max(580, playY - 25);
            var searchMaxY = Math.Min(height - 10, playY + 60);
            var sumX = 0L;
            var sumY = 0L;
            var count = 0;
            var scanned = 0;
            for (var y = searchMinY; y <= searchMaxY; y += 2)
            {
                for (var x = searchMinX; x <= searchMaxX; x += 4)
                {
                    scanned++;
                    var (pr, pg, pb) = SampleRgb(frame.Pixels, stride, x, y);
                    if (IsColoredPlayPixel(pr, pg, pb))
                    {
                        sumX += x;
                        sumY += y;
                        count++;
                    }
                }
            }

            if (scanned > 0 && count >= 12 && count * 100 / scanned >= 10)
            {
                playX = (int)(sumX / count);
                playY = (int)(sumY / count);
                playActive = true;
            }
        }

        return new LauncherWhiteCardState(loginX, loginY, playX, playY, loginActive, playActive);
    }

    /// <summary>The launcher card's near-white background (stricter than <see cref="IsLightCardPixel"/>).</summary>
    private static bool IsCardWhite(byte[] pixels, int stride, int x, int y)
    {
        var (r, g, b) = SampleRgb(pixels, stride, x, y);
        return r > 215 && g > 210 && b > 195;
    }

    private static bool IsLightCardPixel(byte r, byte g, byte b) =>
        r > 200 && g > 160 && b > 145 && Math.Min(r, Math.Min(g, b)) >= 145;

    private static bool IsColoredPlayPixel(byte r, byte g, byte b)
    {
        var min = Math.Min(r, Math.Min(g, b));
        var bright = Math.Max(r, Math.Max(g, b));
        return bright > 110 && (bright - min) >= 35 && min < 175;
    }

    private static (byte R, byte G, byte B) SampleRgb(byte[] pixels, int stride, int x, int y)
    {
        var idx = y * stride + (x * 4);
        return (pixels[idx + 2], pixels[idx + 1], pixels[idx]);
    }

    internal sealed record LauncherWhiteCardState(
        int LoginX,
        int LoginY,
        int PlayX,
        int PlayY,
        bool LoginButtonActive,
        bool PlayButtonActive);

    private sealed record LauncherFrameSnapshot(
        LauncherWhiteCardState? WhiteCard,
        (int X, int Y)? LoginPoint,
        (int X, int Y)? PlayPoint,
        bool OcrReadyVisible,
        bool OcrAutoPlayVisible)
    {
        public bool HasAnySignal =>
            WhiteCard is not null ||
            LoginPoint is not null ||
            PlayPoint is not null ||
            OcrReadyVisible ||
            OcrAutoPlayVisible;
    }
}
