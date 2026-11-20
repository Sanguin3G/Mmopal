using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mmopal.Contracts;
using Mmopal.Native;

namespace Mmopal.Domain.Tests;

public sealed class WindowsOcrReaderTests
{
    [Fact]
    public async Task Windows_ocr_reads_text_rendered_into_a_frame()
    {
        const int width = 280;
        const int height = 100;
        var pixels = RenderText(width, height, "148");
        var frame = new AutomationFrame(DateTimeOffset.UtcNow, new CaptureRegion(0, 0, width, height), pixels);
        var reader = new WindowsOcrReader();
        if (Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages() is null)
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadAsync(frame, frame.Region));
            Assert.Contains("no language installed", error.Message, StringComparison.OrdinalIgnoreCase);
            return;
        }

        var result = await reader.ReadAsync(frame, frame.Region);
        var blank = await reader.ReadAsync(frame, new CaptureRegion(220, 0, 60, height));

        Assert.NotNull(result);
        Assert.Contains("148", result.Text.Replace(" ", string.Empty), StringComparison.Ordinal);
        Assert.Equal(1m, result.Confidence);
        Assert.Null(blank);
    }

    [Fact]
    public async Task Windows_ocr_returns_null_when_the_region_misses_the_frame()
    {
        var frame = new AutomationFrame(DateTimeOffset.UtcNow, new CaptureRegion(0, 0, 2, 2), new byte[16]);
        var reader = new WindowsOcrReader();

        var result = await reader.ReadAsync(frame, new CaptureRegion(20, 20, 4, 4));

        Assert.Null(result);
    }

    [Fact]
    public async Task Windows_ocr_rejects_an_empty_region()
    {
        var frame = new AutomationFrame(DateTimeOffset.UtcNow, new CaptureRegion(0, 0, 2, 2), new byte[16]);
        var reader = new WindowsOcrReader();

        var error = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            reader.ReadAsync(frame, new CaptureRegion(0, 0, 0, 4)));

        Assert.Contains("positive", error.Message, StringComparison.Ordinal);
    }

    private static byte[] RenderText(int width, int height, string text)
    {
        byte[]? pixels = null;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var visual = new DrawingVisual();
                using (var context = visual.RenderOpen())
                {
                    context.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
                    var formatted = new FormattedText(
                        text,
                        CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight,
                        new Typeface("Segoe UI"),
                        48,
                        Brushes.Black,
                        1.0);
                    context.DrawText(formatted, new Point(16, 16));
                }

                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(visual);
                pixels = new byte[width * height * 4];
                bitmap.CopyPixels(pixels, width * 4, 0);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(20)))
        {
            throw new TimeoutException("Rendering the OCR fixture did not finish.");
        }

        if (failure is not null) throw failure;
        return pixels ?? throw new InvalidOperationException("Rendering the OCR fixture produced no pixels.");
    }
}
