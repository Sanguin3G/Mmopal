using System.Reflection;
using System.Text.Json;
using Mmopal.Contracts;

namespace Mmopal.Worker.Vision;

/// <summary>A price read from the screen: copper total plus how it was read, for logs.</summary>
public sealed record CoinReading(long Copper, string Text, double WorstDigitDistance);

/// <summary>
/// Reads Guild Wars 2 coin amounts ("2[gold] 26[silver] 62[copper]") from a frame without OCR.
/// <para>
/// Each number in GW2 is followed by its coin icon, so the icon says which coin the number is: the reader finds the
/// bright blobs, tells compact filled discs (coins) from thin strokes (digits), names each coin by colour (silver is
/// grey, gold is yellow, copper is orange), and recognises digits by shape against templates cut from real frames
/// (embedded <c>gw2-digit-glyphs.json</c>). Windows OCR lost "00" groups, read a coin as a "4" and merged groups;
/// none of that can happen here. When a digit is not a confident match the reader returns null and the caller falls
/// back to OCR.
/// </para>
/// </summary>
public static class CoinPriceReader
{
    internal const int ShapeWidth = 10;
    internal const int ShapeHeight = 16;
    private const int BrightThreshold = 120;
    // A digit is trusted when it is close to a template (mean absolute difference 0..1), or reasonably close and
    // clearly closer than any other digit (the buyer column draws "1" a pixel wider than the seller column).
    internal const double MaxDigitDistance = 0.12;
    internal const double MaxDigitDistanceWithMargin = 0.22;
    internal const double MinRunnerUpMargin = 0.12;

    internal static bool IsConfident(double distance, double runnerUp) =>
        distance <= MaxDigitDistance || (distance <= MaxDigitDistanceWithMargin && runnerUp - distance >= MinRunnerUpMargin);

    private static readonly Lazy<List<DigitTemplate>> Templates = new(LoadTemplates);
    private static readonly object LearnLock = new();

    /// <summary>Where digit shapes learned at runtime are kept (added to the embedded set).</summary>
    public static string LearnedTemplatesPath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mmopal", "digit-glyphs.json");

    internal enum Coin { Gold, Silver, Copper }

    internal sealed record Blob(int X, int Y, int Width, int Height, int Pixels, double MeanSaturation, double MeanGreenRed, float[] Shape)
    {
        public double Fill => Pixels / (double)(Width * Height);
        public double Aspect => Width / (double)Height;
    }

    internal sealed record DigitTemplate(int Digit, double Aspect, float[] Shape);

    // Icon is null when the group was closed by a wide gap (the tiny silver coin in list rows is sometimes too dim).
    internal sealed record Group(Coin Coin, IReadOnlyList<Blob> Digits, Blob? Icon);

    /// <summary>The first (topmost) coin amount in <paramref name="region"/>, or null when it cannot be read with confidence.</summary>
    public static CoinReading? ReadFirst(AutomationFrame frame, CaptureRegion region) =>
        ReadAll(frame, region).FirstOrDefault();

    /// <summary>Every readable coin amount in the region, top to bottom (one per text line).</summary>
    public static IReadOnlyList<CoinReading> ReadAll(AutomationFrame frame, CaptureRegion region)
    {
        var readings = new List<CoinReading>();
        foreach (var line in SegmentLines(frame, region))
        {
            CoinReading? reading;
            lock (LearnLock)
            {
                // Learning adds to the same list; read under the same lock.
                reading = Assemble(line, Templates.Value);
            }

            if (reading is not null)
            {
                readings.Add(reading);
            }
        }

        return readings;
    }

    /// <summary>Text lines in the region, each split into digit groups that end with their coin icon.</summary>
    internal static IReadOnlyList<IReadOnlyList<Group>> SegmentLines(AutomationFrame frame, CaptureRegion region)
    {
        // Scrollbars, highlighted rows and dialog edges are far taller or wider than any digit; left in, they chain
        // neighbouring rows into one "line".
        var blobs = FindBlobs(frame, region).Where(b => b.Height <= 30 && b.Width <= 40).ToList();
        var lines = new List<IReadOnlyList<Group>>();
        foreach (var line in GroupIntoLines(blobs))
        {
            var groups = SplitIntoGroups(line);
            if (groups.Count > 0)
            {
                lines.Add(groups);
            }
        }

        return lines;
    }

    private static CoinReading? Assemble(IReadOnlyList<Group> groups, IReadOnlyList<DigitTemplate> templates)
    {
        if (templates.Count == 0)
        {
            return null;
        }

        long gold = 0, silver = 0, copper = 0;
        var seen = new HashSet<Coin>();
        var worst = 0.0;
        var text = new List<string>();
        var lastCoin = -1;
        foreach (var group in groups)
        {
            if (!seen.Add(group.Coin) || (int)group.Coin <= lastCoin)
            {
                return null; // a coin twice, or out of gold → silver → copper order
            }

            lastCoin = (int)group.Coin;
            long value = 0;
            foreach (var digit in group.Digits)
            {
                var (best, distance, runnerUp) = Classify(digit, templates);
                if (best < 0 || !IsConfident(distance, runnerUp))
                {
                    return null;
                }

                worst = Math.Max(worst, distance);
                value = value * 10 + best;
            }

            if (group.Digits.Count == 0 || (group.Coin != Coin.Gold && (value > 99 || group.Digits.Count > 2)))
            {
                return null;
            }

            switch (group.Coin)
            {
                case Coin.Gold: gold = value; break;
                case Coin.Silver: silver = value; break;
                default: copper = value; break;
            }

            text.Add($"{value}{group.Coin.ToString()[0].ToString().ToLowerInvariant()}");
        }

        return text.Count == 0 ? null : new CoinReading(gold * 10_000 + silver * 100 + copper, string.Join(" ", text), worst);
    }

    internal static (int Digit, double Distance, double RunnerUp) Classify(Blob blob, IReadOnlyList<DigitTemplate> templates)
    {
        var bestPerDigit = new double[10];
        Array.Fill(bestPerDigit, double.MaxValue);
        foreach (var template in templates)
        {
            var distance = 0.0;
            for (var i = 0; i < blob.Shape.Length; i++)
            {
                distance += Math.Abs(blob.Shape[i] - template.Shape[i]);
            }

            // Shape is stretched to a fixed box, so "1" would look like a fat bar: aspect keeps it apart.
            distance = distance / blob.Shape.Length + Math.Abs(blob.Aspect - template.Aspect) * 0.35;
            if (template.Digit is >= 0 and <= 9 && distance < bestPerDigit[template.Digit])
            {
                bestPerDigit[template.Digit] = distance;
            }
        }

        var best = -1;
        for (var d = 0; d < 10; d++)
        {
            if (bestPerDigit[d] < double.MaxValue && (best < 0 || bestPerDigit[d] < bestPerDigit[best])) best = d;
        }

        if (best < 0)
        {
            return (-1, double.MaxValue, double.MaxValue);
        }

        var runnerUp = Enumerable.Range(0, 10).Where(d => d != best).Select(d => bestPerDigit[d]).Min();
        return (best, bestPerDigit[best], runnerUp);
    }

    /// <summary>Bright 8-connected blobs inside the region (frame coordinates).</summary>
    internal static List<Blob> FindBlobs(AutomationFrame frame, CaptureRegion region)
    {
        var frameWidth = frame.Region.Width;
        var stride = frame.Stride > 0 ? frame.Stride : frameWidth * 4;
        var x0 = Math.Max(0, region.X - frame.Region.X);
        var y0 = Math.Max(0, region.Y - frame.Region.Y);
        var w = Math.Min(region.Width, frameWidth - x0);
        var h = Math.Min(region.Height, frame.Region.Height - y0);
        if (w <= 0 || h <= 0 || frame.Pixels.Length < stride * frame.Region.Height)
        {
            return [];
        }

        var bright = new byte[w * h];
        for (var y = 0; y < h; y++)
        {
            var row = (y0 + y) * stride;
            for (var x = 0; x < w; x++)
            {
                var i = row + (x0 + x) * 4;
                var max = Math.Max(frame.Pixels[i], Math.Max(frame.Pixels[i + 1], frame.Pixels[i + 2]));
                bright[y * w + x] = max >= BrightThreshold ? (byte)1 : (byte)0;
            }
        }

        var blobs = new List<Blob>();
        var label = new int[w * h];
        var stack = new Stack<int>();
        var members = new List<int>();
        var next = 0;
        for (var start = 0; start < bright.Length; start++)
        {
            if (bright[start] == 0 || label[start] != 0) continue;
            next++;
            members.Clear();
            stack.Push(start);
            label[start] = next;
            while (stack.Count > 0)
            {
                var p = stack.Pop();
                members.Add(p);
                int px = p % w, py = p / w;
                for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                {
                    int nx = px + dx, ny = py + dy;
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                    var n = ny * w + nx;
                    if (bright[n] == 0 || label[n] != 0) continue;
                    label[n] = next;
                    stack.Push(n);
                }
            }

            if (members.Count < 4) continue;
            blobs.Add(Describe(frame.Pixels, stride, x0, y0, w, members, frame.Region.X + x0, frame.Region.Y + y0));
        }

        return blobs;
    }

    private static Blob Describe(byte[] pixels, int stride, int x0, int y0, int w, List<int> members, int regionX, int regionY)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = 0, maxY = 0;
        double satSum = 0, grSum = 0;
        foreach (var p in members)
        {
            int x = p % w, y = p / w;
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            var i = (y0 + y) * stride + (x0 + x) * 4;
            double b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
            var max = Math.Max(r, Math.Max(g, b));
            satSum += (max - Math.Min(r, Math.Min(g, b))) / max;
            grSum += r > 0 ? g / r : 1;
        }

        int bw = maxX - minX + 1, bh = maxY - minY + 1;
        // Brightness (max channel) of the bounding box, area-sampled into a fixed grid and normalised 0..1.
        var shape = new float[ShapeWidth * ShapeHeight];
        double lo = 255, hi = 0;
        var cells = new double[ShapeWidth * ShapeHeight];
        for (var cy = 0; cy < ShapeHeight; cy++)
        for (var cx = 0; cx < ShapeWidth; cx++)
        {
            int sx0 = minX + cx * bw / ShapeWidth, sx1 = Math.Max(sx0 + 1, minX + (cx + 1) * bw / ShapeWidth);
            int sy0 = minY + cy * bh / ShapeHeight, sy1 = Math.Max(sy0 + 1, minY + (cy + 1) * bh / ShapeHeight);
            double sum = 0; var count = 0;
            for (var y = sy0; y < sy1; y++)
            for (var x = sx0; x < sx1; x++)
            {
                var i = (y0 + y) * stride + (x0 + x) * 4;
                sum += Math.Max(pixels[i], Math.Max(pixels[i + 1], pixels[i + 2]));
                count++;
            }

            var v = sum / Math.Max(1, count);
            cells[cy * ShapeWidth + cx] = v;
            lo = Math.Min(lo, v); hi = Math.Max(hi, v);
        }

        var range = Math.Max(1, hi - lo);
        for (var k = 0; k < cells.Length; k++) shape[k] = (float)((cells[k] - lo) / range);

        return new Blob(regionX + minX, regionY + minY, bw, bh, members.Count, satSum / members.Count, grSum / members.Count, shape);
    }

    /// <summary>Clusters blobs into text lines by vertical centre (within 5 px of the line's first blob), top to bottom.</summary>
    private static List<List<Blob>> GroupIntoLines(List<Blob> blobs)
    {
        var lines = new List<(double Center, List<Blob> Blobs)>();
        foreach (var blob in blobs.OrderBy(b => b.Y + b.Height / 2.0))
        {
            var center = blob.Y + blob.Height / 2.0;
            var index = lines.FindIndex(l => Math.Abs(l.Center - center) <= 5);
            if (index < 0) lines.Add((center, [blob]));
            else lines[index].Blobs.Add(blob);
        }

        return lines.Select(l => l.Blobs).ToList();
    }

    /// <summary>
    /// Left to right: digits accumulate until a coin icon (or a gap wider than half a digit height) closes the group.
    /// Coins are compact filled discs no taller than the digits; digits are thin strokes. Blobs much smaller than a
    /// digit (dots, spinner arrows) or much bigger (a highlighted row, a scrollbar) are ignored. Coins are then named
    /// right to left (copper last, then silver, then gold), which is how GW2 always lays them out; icon colours, where
    /// an icon was seen, must agree.
    /// </summary>
    private static List<Group> SplitIntoGroups(List<Blob> line)
    {
        var result = new List<Group>();
        var ordered = line.OrderBy(b => b.X).ToList();
        // Median of everything at least 7 px tall: digits outnumber coins on every price line, and bold row digits are as
        // "filled" as coins, so fill cannot be used here.
        var heights = ordered.Where(b => b.Height is >= 7 and <= 30).Select(b => b.Height).OrderBy(h => h).ToArray();
        if (heights.Length == 0)
        {
            return result;
        }

        var digitHeight = heights[heights.Length / 2];
        var spans = new List<(List<Blob> Digits, Blob? Icon)>();
        var pending = new List<Blob>();
        Blob? previous = null;
        foreach (var blob in ordered)
        {
            if (blob.Height < digitHeight * 0.45 || blob.Height > digitHeight * 1.6 || blob.Width > digitHeight * 2.5)
            {
                continue;
            }

            if (IsCoin(blob, digitHeight))
            {
                if (pending.Count > 0) spans.Add((pending, blob));
                pending = [];
                previous = null;
                continue;
            }

            if (blob.Height < digitHeight * 0.8 || blob.Height > digitHeight * 1.25)
            {
                continue;
            }

            if (previous is not null && blob.X - (previous.X + previous.Width) > digitHeight * 0.5 && pending.Count > 0)
            {
                spans.Add((pending, null));
                pending = [];
            }

            pending.Add(blob);
            previous = blob;
        }

        // The last number must end with a coin we actually saw: otherwise a lost copper part ("14s 18c" seen as "14")
        // would be read as a smaller, wrong amount instead of no amount.
        if (spans.Count is 0 or > 3 || spans[^1].Icon is null)
        {
            return result;
        }

        for (var i = 0; i < spans.Count; i++)
        {
            var coin = (Coin)(3 - spans.Count + i);
            if (spans[i].Icon is { } icon && CoinOf(icon) != coin)
            {
                return []; // the layout and the icon colours disagree: do not guess
            }

            result.Add(new Group(coin, spans[i].Digits, spans[i].Icon));
        }

        return result;
    }

    internal static bool IsCoin(Blob blob, int digitHeight) =>
        blob.Fill >= 0.5 &&
        blob.Aspect is >= 0.75 and <= 1.6 &&
        blob.Height <= digitHeight * 0.95;

    internal static Coin CoinOf(Blob icon) =>
        icon.MeanSaturation <= 0.25 ? Coin.Silver
        : icon.MeanGreenRed >= GoldGreenRedThreshold ? Coin.Gold
        : Coin.Copper;

    // Mean green/red of an icon: gold coins are yellow (high), copper coins orange (lower).
    internal const double GoldGreenRedThreshold = 0.76;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static List<DigitTemplate> LoadTemplates()
    {
        var templates = new List<DigitTemplate>();
        using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Mmopal.Worker.Vision.gw2-digit-glyphs.json"))
        {
            if (stream is not null)
            {
                templates.AddRange(JsonSerializer.Deserialize<List<DigitTemplate>>(stream, JsonOptions) ?? []);
            }
        }

        try
        {
            if (File.Exists(LearnedTemplatesPath))
            {
                templates.AddRange(JsonSerializer.Deserialize<List<DigitTemplate>>(File.ReadAllText(LearnedTemplatesPath), JsonOptions) ?? []);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A damaged learned file only costs the learned shapes.
        }

        return templates;
    }

    /// <summary>
    /// After a price was confirmed (so the line is known to show <paramref name="expectedCopper"/>), learn the shapes
    /// of digits the templates do not know yet. Only when the line splits into the expected digit counts and every digit
    /// that is recognised agrees with the expected value; at most two unknown digits per line. Returns how many shapes
    /// were learned.
    /// </summary>
    public static int LearnFromConfirmed(AutomationFrame frame, CaptureRegion region, long expectedCopper)
    {
        var lines = SegmentLines(frame, region);
        if (lines.Count == 0 || expectedCopper < 0)
        {
            return 0;
        }

        var learned = new List<DigitTemplate>();
        lock (LearnLock)
        {
            var templates = Templates.Value;
            var groups = lines[0];
            var expected = ExpectedGroupDigits(expectedCopper, groups.Count);
            if (expected is null || expected.Count != groups.Count)
            {
                return 0;
            }

            for (var g = 0; g < groups.Count; g++)
            {
                if (groups[g].Digits.Count != expected[g].Length)
                {
                    return 0;
                }

                for (var d = 0; d < expected[g].Length; d++)
                {
                    var blob = groups[g].Digits[d];
                    var want = expected[g][d] - '0';
                    var (best, distance, _) = templates.Count == 0 ? (-1, double.MaxValue, double.MaxValue) : Classify(blob, templates);
                    if (distance <= MaxDigitDistance)
                    {
                        if (best != want) return 0; // a confident digit disagrees: the line is not what we think
                        continue;
                    }

                    learned.Add(new DigitTemplate(want, blob.Aspect, blob.Shape));
                }
            }

            if (learned.Count is 0 or > 2)
            {
                return 0;
            }

            templates.AddRange(learned);
            try
            {
                var saved = File.Exists(LearnedTemplatesPath)
                    ? JsonSerializer.Deserialize<List<DigitTemplate>>(File.ReadAllText(LearnedTemplatesPath), JsonOptions) ?? []
                    : [];
                saved.AddRange(learned);
                Directory.CreateDirectory(Path.GetDirectoryName(LearnedTemplatesPath)!);
                File.WriteAllText(LearnedTemplatesPath, JsonSerializer.Serialize(saved.TakeLast(400).ToList(), JsonOptions));
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // Still learned for this run.
            }
        }

        return learned.Count;
    }

    /// <summary>Digit strings of the last <paramref name="groupCount"/> coin groups (gold, silver, copper order).</summary>
    internal static IReadOnlyList<string>? ExpectedGroupDigits(long copper, int groupCount)
    {
        if (groupCount is < 1 or > 3)
        {
            return null;
        }

        var all = new[] { (copper / 10_000).ToString(System.Globalization.CultureInfo.InvariantCulture), (copper / 100 % 100).ToString(System.Globalization.CultureInfo.InvariantCulture), (copper % 100).ToString(System.Globalization.CultureInfo.InvariantCulture) };
        // Higher coins can only be left out when they are 0 (rows omit a zero gold; the Total line shows it).
        for (var skipped = 0; skipped < 3 - groupCount; skipped++)
        {
            if (all[skipped] != "0") return null;
        }

        return all[(3 - groupCount)..];
    }

    /// <summary>For tests and template building: read with a given template set.</summary>
    internal static IReadOnlyList<CoinReading> ReadAll(AutomationFrame frame, CaptureRegion region, IReadOnlyList<DigitTemplate> templates) =>
        SegmentLines(frame, region).Select(line => Assemble(line, templates)).OfType<CoinReading>().ToArray();
}
