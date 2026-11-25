using System.Runtime.InteropServices;

namespace Mmopal.Native;

/// <summary>A text drawn large: brightness per pixel, and where each letter starts (one more entry: where the text ends).</summary>
public sealed record DrawnText(byte[] Pixels, int Width, int Height, IReadOnlyList<int> LetterX);

/// <summary>
/// Draws text in the font of the Trading Post's lists and search box, Trebuchet MS, which comes with Windows. Drawn large
/// and with kerning, so the reader can shrink it to the game's fractional size: at the game's size Windows rounds every
/// letter to whole pixels, which the game does not (median match 0.63 against 0.90, frames of 20-24 Nov).
/// </summary>
public static class GameFont
{
    /// <summary>Size of the drawing, in pixels per em.</summary>
    public const int Em = 96;
    private const string Face = "Trebuchet MS";
    private const int CacheSize = 256;
    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, DrawnText> Cache = new(StringComparer.Ordinal);
    private static Dictionary<(char, char), int>? _kerning;
    private static bool? _installed;

    /// <summary>The text, or null when the font is not installed.</summary>
    public static DrawnText? Draw(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        lock (Gate)
        {
            if (Cache.TryGetValue(text, out var known))
            {
                return known;
            }

            if (_installed == false || DrawNew(text) is not { } drawn)
            {
                return null;
            }

            if (Cache.Count >= CacheSize) Cache.Clear();
            return Cache[text] = drawn;
        }
    }

    private static DrawnText? DrawNew(string text)
    {
        var dc = CreateCompatibleDC(IntPtr.Zero);
        var font = CreateFontW(-Em, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 4, 0, Face);
        var dib = IntPtr.Zero;
        var oldFont = SelectObject(dc, font);
        var oldBitmap = IntPtr.Zero;
        try
        {
            var face = new char[64];
            var length = GetTextFaceW(dc, face.Length, face);
            _installed ??= new string(face, 0, Math.Max(0, length)).TrimEnd((char)0).Equals(Face, StringComparison.OrdinalIgnoreCase);
            if (_installed == false)
            {
                return null;
            }

            _kerning ??= ReadKerning(dc);
            var letters = new List<int>(text.Length + 1);
            var x = Em / 4;
            for (var i = 0; i < text.Length; i++)
            {
                letters.Add(x);
                if (!GetTextExtentPoint32W(dc, text[i].ToString(), 1, out var size)) return null;
                x += size.Width;
                if (i + 1 < text.Length && _kerning.TryGetValue((text[i], text[i + 1]), out var kern)) x += kern;
            }

            letters.Add(x);
            var (width, height) = (x + Em / 4, Em * 3 / 2);
            var info = new BitmapInfo { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
            dib = CreateDIBSection(dc, ref info, 0, out var bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero) return null;
            oldBitmap = SelectObject(dc, dib);
            SetBkMode(dc, 1);
            SetTextColor(dc, 0xFFFFFF);
            for (var i = 0; i < text.Length; i++)
            {
                TextOutW(dc, letters[i], Em / 8, text[i].ToString(), 1);
            }

            GdiFlush();
            var raw = new byte[width * height * 4];
            Marshal.Copy(bits, raw, 0, raw.Length);
            var pixels = new byte[width * height];
            for (var i = 0; i < pixels.Length; i++) pixels[i] = raw[i * 4 + 1];
            return new DrawnText(pixels, width, height, letters);
        }
        finally
        {
            if (oldBitmap != IntPtr.Zero) SelectObject(dc, oldBitmap);
            SelectObject(dc, oldFont);
            if (dib != IntPtr.Zero) DeleteObject(dib);
            DeleteObject(font);
            DeleteDC(dc);
        }
    }

    private static Dictionary<(char, char), int> ReadKerning(IntPtr dc)
    {
        var pairs = new Dictionary<(char, char), int>();
        var count = GetKerningPairsW(dc, 0, null);
        if (count == 0) return pairs;
        var read = new KerningPair[count];
        GetKerningPairsW(dc, count, read);
        foreach (var pair in read) pairs[((char)pair.First, (char)pair.Second)] = pair.Amount;
        return pairs;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public int Size, Width, Height;
        public short Planes, BitCount;
        public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant, Colors;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KerningPair
    {
        public ushort First, Second;
        public int Amount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TextSize
    {
        public int Width, Height;
    }

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFontW(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strikeOut, uint charSet, uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);

    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] private static extern uint SetTextColor(IntPtr dc, uint color);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern int GetTextFaceW(IntPtr dc, int count, [Out] char[] face);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern bool TextOutW(IntPtr dc, int x, int y, string text, int count);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern bool GetTextExtentPoint32W(IntPtr dc, string text, int count, out TextSize size);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern uint GetKerningPairsW(IntPtr dc, uint count, [Out] KerningPair[]? pairs);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
}
