using Mmopal.Contracts;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using OcrResult = Mmopal.Contracts.OcrResult;

namespace Mmopal.Native;

/// <summary>
/// Reads text from a saved BGRA32 frame with the Windows OCR engine.
/// The engine does not report a confidence score, so a recognized line uses 1.
/// This reader does not capture the desktop.
/// </summary>
public sealed class WindowsOcrReader : IOcrReader, IUiTextLocator
{
    public async Task<OcrResult?> ReadAsync(
        AutomationFrame frame,
        CaptureRegion region,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(region);
        cancellationToken.ThrowIfCancellationRequested();

        var pixels = FrameVision.CopyBgra(frame, region, out var width, out var height);
        if (pixels is null) return null;

        var engine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is null)
        {
            throw new InvalidOperationException("Windows OCR has no language installed for the current user.");
        }

        using var bitmap = CreateBitmap(pixels, width, height);
        cancellationToken.ThrowIfCancellationRequested();
        var recognized = await engine.RecognizeAsync(bitmap);
        var text = recognized.Text.Trim();
        return text.Length == 0 ? null : new OcrResult(text, 1m);
    }

    public async Task<IReadOnlyList<OcrTextBox>> LocateLinesAsync(
        AutomationFrame frame,
        CaptureRegion region,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(region);
        cancellationToken.ThrowIfCancellationRequested();

        byte[]? pixels;
        int width;
        int height;
        try
        {
            pixels = FrameVision.CopyBgra(frame, region, out width, out height);
        }
        catch (Exception)
        {
            return Array.Empty<OcrTextBox>();
        }

        if (pixels is null || width <= 0 || height <= 0)
        {
            return Array.Empty<OcrTextBox>();
        }

        try
        {
            var engine = OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine is null)
            {
                return Array.Empty<OcrTextBox>();
            }

            using var bitmap = CreateBitmap(pixels, width, height);
            cancellationToken.ThrowIfCancellationRequested();
            var recognized = await engine.RecognizeAsync(bitmap);
            if (recognized.Lines.Count == 0)
            {
                return Array.Empty<OcrTextBox>();
            }

            var originX = Math.Max(frame.Region.X, region.X);
            var originY = Math.Max(frame.Region.Y, region.Y);
            var boxes = new List<OcrTextBox>(recognized.Lines.Count);
            foreach (var line in recognized.Lines)
            {
                var lineText = line.Text?.Trim();
                if (string.IsNullOrWhiteSpace(lineText) || line.Words.Count == 0)
                {
                    continue;
                }

                var minX = double.MaxValue;
                var minY = double.MaxValue;
                var maxX = double.MinValue;
                var maxY = double.MinValue;
                foreach (var word in line.Words)
                {
                    var rect = word.BoundingRect;
                    if (rect.Width <= 0 || rect.Height <= 0)
                    {
                        continue;
                    }

                    if (rect.X < minX) minX = rect.X;
                    if (rect.Y < minY) minY = rect.Y;
                    if (rect.X + rect.Width > maxX) maxX = rect.X + rect.Width;
                    if (rect.Y + rect.Height > maxY) maxY = rect.Y + rect.Height;
                }

                if (maxX <= minX || maxY <= minY)
                {
                    continue;
                }

                var x = originX + Math.Clamp((int)Math.Floor(minX), 0, Math.Max(0, width - 1));
                var y = originY + Math.Clamp((int)Math.Floor(minY), 0, Math.Max(0, height - 1));
                var w = Math.Max(1, Math.Min(originX + width - x, (int)Math.Ceiling(maxX - minX)));
                var h = Math.Max(1, Math.Min(originY + height - y, (int)Math.Ceiling(maxY - minY)));
                boxes.Add(new OcrTextBox(lineText, x, y, w, h));
            }

            return boxes;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Array.Empty<OcrTextBox>();
        }
    }

    private static SoftwareBitmap CreateBitmap(byte[] pixels, int width, int height) =>
        SoftwareBitmap.CreateCopyFromBuffer(CopyToBuffer(pixels), BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Ignore);

    private static IBuffer CopyToBuffer(byte[] pixels)
    {
        using var writer = new DataWriter();
        writer.WriteBytes(pixels);
        return writer.DetachBuffer();
    }
}
