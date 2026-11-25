using Mmopal.Contracts;
using Mmopal.Native;

namespace Mmopal.Worker.Vision;

/// <summary>Where a name is drawn: its pen start, the top of its line, the letter size, and which channel shows it best.</summary>
/// <param name="DarkestChannel">White text: the darkest channel, which the red spelling underline does not touch.</param>
public sealed record NameField(double PenX, double Top, double Em, bool DarkestChannel = false);

/// <summary>How well the name expected at a place matches what is there.</summary>
/// <param name="Overall">Correlation of the whole name, 0..1.</param>
/// <param name="WorstLetter">Correlation of its least similar letter.</param>
/// <param name="Letter">That letter.</param>
/// <param name="DigitsClear">Every digit matches better than any other digit in its place.</param>
/// <param name="EndsThere">Nothing is written after it ("+1" is not "+16").</param>
public sealed record NameMatch(double Overall, double WorstLetter, char Letter, bool DigitsClear, bool EndsThere)
{
    public static readonly NameMatch None = new(0, 0, ' ', false, false);

    /// <summary>The name is there, letter for letter.</summary>
    public bool IsProof => Overall >= DrawnNameReader.MinOverall && WorstLetter >= DrawnNameReader.MinLetter && DigitsClear && EndsThere;
}

/// <summary>
/// Tells whether an expected item name is on screen by drawing it and comparing pictures, letter by letter. Reading
/// arbitrary text is what Windows OCR gets wrong on the small coloured names of the Trading Post lists (121, 23 and 40
/// names not found on the nights of 22-25 Nov); the question here is only ever whether it is this one name.
/// </summary>
public static class DrawnNameReader
{
    // Measured: the right name on 45 frames scored 0.90-0.93 with its worst letter at 0.78-0.90. Of 83,328 other names of
    // GW2 items on them none came near but "+15" for "+16" (worst letter 0.79, which the digits decide); the next was 0.51.
    internal const double MinOverall = 0.84;
    internal const double MinLetter = 0.74;
    private const double MinDigitLead = 0.01;
    private const int MinCellWidth = 6;
    private const int BlankAfter = 10;

    public static NameMatch Compare(AutomationFrame frame, NameField field, string name)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(field);
        if (frame.Pixels.Length == 0 || GameFont.Draw(name) is not { } drawn)
        {
            return NameMatch.None;
        }

        var scale = field.Em / GameFont.Em;
        // Window of the frame that holds the name: from just before the pen to a little past its end.
        var left = (int)Math.Floor(field.PenX) - 4;
        var top = (int)Math.Floor(field.Top) - 3;
        var width = (int)Math.Ceiling((drawn.LetterX[^1] - drawn.LetterX[0]) * scale) + 8 + BlankAfter;
        var height = (int)Math.Ceiling(drawn.Height * scale * 0.86) + 6;
        if (Brightness(frame, left, top, width, height, field.DarkestChannel) is not { } seen)
        {
            return NameMatch.None;
        }

        (double Score, double X, double Y, float[] Picture) best = (-1, 0, 0, []);
        var integral = Integral(drawn);
        for (var dy = -1.0; dy <= 1.0; dy += 0.25)
        {
            for (var dx = -1.0; dx <= 1.0; dx += 0.25)
            {
                var (x, y) = (field.PenX + dx - left, field.Top + dy - top);
                var picture = Shrink(drawn, integral, scale, x, y, width, height);
                var score = Correlation(picture, seen, width, 0, width - BlankAfter, height, 0);
                if (score > best.Score) best = (score, x, y, picture);
            }
        }

        if (best.Score < 0.5)
        {
            return NameMatch.None with { Overall = Math.Max(0, best.Score) };
        }

        var (worst, letter, digitsClear) = (1.0, ' ', true);
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsWhiteSpace(name[i])) continue;
            var (from, to) = Cell(drawn, scale, best.X, i, width);
            var similarity = BestShift(best.Picture, seen, width, height, from, to);
            if (similarity < worst) (worst, letter) = (similarity, name[i]);
            if (char.IsAsciiDigit(name[i]) && digitsClear)
            {
                digitsClear = LeadsOtherDigits(name, i, similarity, scale, best.X, best.Y, seen, width, height);
            }
        }

        return new NameMatch(best.Score, worst, letter, digitsClear, EndsThere(drawn, scale, best.X, best.Picture, seen, width, height));
    }

    /// <summary>Columns of the letter, at least <see cref="MinCellWidth"/> wide ("i" and "l" say nothing on their own).</summary>
    private static (int From, int To) Cell(DrawnText drawn, double scale, double penX, int index, int width)
    {
        var start = penX + (drawn.LetterX[index] - drawn.LetterX[0]) * scale;
        var end = penX + (drawn.LetterX[index + 1] - drawn.LetterX[0]) * scale;
        var pad = Math.Max(0, (MinCellWidth - (end - start)) / 2);
        return (Math.Max(0, (int)Math.Floor(start - pad)), Math.Min(width, (int)Math.Ceiling(end + pad)));
    }

    /// <summary>Best of the letter's own place and one pixel either side: spacing is rounded differently now and then.</summary>
    private static double BestShift(float[] picture, float[] seen, int width, int height, int from, int to) =>
        Math.Max(
            Correlation(picture, seen, width, from, to, height, 0),
            Math.Max(Correlation(picture, seen, width, from, to, height, -1), Correlation(picture, seen, width, from, to, height, 1)));

    private static bool LeadsOtherDigits(string name, int index, double own, double scale, double penX, double topY, float[] seen, int width, int height)
    {
        for (var digit = '0'; digit <= '9'; digit++)
        {
            if (digit == name[index] || GameFont.Draw(name[..index] + digit + name[(index + 1)..]) is not { } other)
            {
                continue;
            }

            var picture = Shrink(other, Integral(other), scale, penX, topY, width, height);
            var (from, to) = Cell(other, scale, penX, index, width);
            if (BestShift(picture, seen, width, height, from, to) + MinDigitLead > own)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>No ink right after the name, measured against the name's own brightness.</summary>
    private static bool EndsThere(DrawnText drawn, double scale, double penX, float[] picture, float[] seen, int width, int height)
    {
        var end = (int)Math.Ceiling(penX + (drawn.LetterX[^1] - drawn.LetterX[0]) * scale) + 1;
        double ink = 0, paper = 0;
        int inkCount = 0, paperCount = 0;
        for (var i = 0; i < seen.Length; i++)
        {
            if (i % width >= end) continue;
            if (picture[i] > 150) (ink, inkCount) = (ink + seen[i], inkCount + 1);
            else if (picture[i] < 5) (paper, paperCount) = (paper + seen[i], paperCount + 1);
        }

        if (inkCount == 0 || paperCount == 0) return false;
        var limit = paper / paperCount + (ink / inkCount - paper / paperCount) * 0.6;
        // Letter rows only: the frame of the row or the red underline may pass below.
        var (firstRow, lastRow) = LetterRows(picture, width, height);
        for (var y = firstRow; y <= lastRow; y++)
        {
            for (var x = Math.Max(0, end); x < Math.Min(width, end + BlankAfter - 2); x++)
            {
                if (seen[y * width + x] > limit) return false;
            }
        }

        return true;
    }

    private static (int First, int Last) LetterRows(float[] picture, int width, int height)
    {
        var (first, last) = (height, -1);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (picture[y * width + x] < 100) continue;
                first = Math.Min(first, y);
                last = Math.Max(last, y);
            }
        }

        return (first, last);
    }

    private static float[]? Brightness(AutomationFrame frame, int left, int top, int width, int height, bool darkest)
    {
        var stride = frame.Stride > 0 ? frame.Stride : frame.Region.Width * 4;
        var (x0, y0) = (left - frame.Region.X, top - frame.Region.Y);
        if (x0 < 0 || y0 < 0 || x0 + width > frame.Region.Width || y0 + height > frame.Region.Height || frame.Pixels.Length < stride * frame.Region.Height)
        {
            return null;
        }

        var values = new float[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y0 + y) * stride + (x0 + x) * 4;
                byte b = frame.Pixels[i], g = frame.Pixels[i + 1], r = frame.Pixels[i + 2];
                values[y * width + x] = darkest ? Math.Min(b, Math.Min(g, r)) : Math.Max(b, Math.Max(g, r));
            }
        }

        return values;
    }

    /// <summary>Summed-area table of the drawing, one row and column larger.</summary>
    private static int[] Integral(DrawnText drawn)
    {
        var w = drawn.Width + 1;
        var table = new int[w * (drawn.Height + 1)];
        for (var y = 0; y < drawn.Height; y++)
        {
            var row = 0;
            for (var x = 0; x < drawn.Width; x++)
            {
                row += drawn.Pixels[y * drawn.Width + x];
                table[(y + 1) * w + x + 1] = table[y * w + x + 1] + row;
            }
        }

        return table;
    }

    /// <summary>The drawing at the game's size with its pen at the given place: each pixel is the average of the area it covers.</summary>
    private static float[] Shrink(DrawnText drawn, int[] integral, double scale, double penX, double topY, int width, int height)
    {
        var picture = new float[width * height];
        var tableWidth = drawn.Width + 1;
        var columns = new int[width + 1];
        var rows = new int[height + 1];
        for (var x = 0; x <= width; x++) columns[x] = Math.Clamp((int)Math.Round((x - penX) / scale) + drawn.LetterX[0], 0, drawn.Width);
        for (var y = 0; y <= height; y++) rows[y] = Math.Clamp((int)Math.Round((y - topY) / scale), 0, drawn.Height);
        var area = 1 / (scale * scale);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                int x0 = columns[x], x1 = columns[x + 1], y0 = rows[y], y1 = rows[y + 1];
                var sum = integral[y1 * tableWidth + x1] - integral[y0 * tableWidth + x1] - integral[y1 * tableWidth + x0] + integral[y0 * tableWidth + x0];
                picture[y * width + x] = (float)(sum / area);
            }
        }

        return picture;
    }

    /// <summary>Normalised correlation of a range of columns of both pictures, the seen one shifted sideways.</summary>
    private static double Correlation(float[] picture, float[] seen, int width, int from, int to, int height, int shift)
    {
        double a = 0, b = 0, aa = 0, bb = 0, ab = 0;
        var n = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = from; x < to; x++)
            {
                var sx = x + shift;
                if (sx < 0 || sx >= width) continue;
                double p = picture[y * width + x], s = seen[y * width + sx];
                a += p;
                b += s;
                aa += p * p;
                bb += s * s;
                ab += p * s;
                n++;
            }
        }

        if (n == 0) return 0;
        var (va, vb) = (aa - a * a / n, bb - b * b / n);
        return va < 1 || vb < 1 ? 0 : (ab - a * b / n) / Math.Sqrt(va * vb);
    }
}
