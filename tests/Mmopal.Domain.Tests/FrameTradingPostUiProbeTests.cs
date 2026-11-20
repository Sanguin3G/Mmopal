using System.Buffers.Binary;
using Mmopal.Contracts;
using Mmopal.Native;

namespace Mmopal.Domain.Tests;

public sealed class FrameTradingPostUiProbeTests
{
    [Fact]
    public async Task Screen_probe_detects_a_template_and_reads_the_price_digits()
    {
        var root = Path.Combine(Path.GetTempPath(), "mmopal-templates-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var pattern = new byte[] { 9, 8, 7, 1, 2, 3, 4, 5, 6, 9, 9, 9 };
            File.WriteAllBytes(Path.Combine(root, "tradingpost_tab.png"), BottomUp24BitBmp(2, 2, pattern));
            File.WriteAllBytes(Path.Combine(root, "price_2.png"), BottomUp24BitBmp(2, 2, pattern));
            var frame = new AutomationFrame(
                DateTimeOffset.UtcNow,
                new CaptureRegion(0, 0, 3, 2),
                [
                    0, 0, 0, 255, 9, 8, 7, 255, 1, 2, 3, 255,
                    0, 0, 0, 255, 4, 5, 6, 255, 9, 9, 9, 255
                ]);
            var probe = new FrameTradingPostUiProbe(new FixedCapture(frame), new FrameTemplateMatcher(), new FixedOcr("12"));
            var options = new TradingPostUiProbeOptions(root, frame.Region, MatchScore: 0.99m, VisibleItemName: "Sample");

            var snapshot = await probe.ProbeAsync(options);
            var price = await probe.ReadVisiblePriceAsync(options);

            Assert.True(snapshot.TradingPostOpen);
            Assert.False(snapshot.ItemWindowOpen);
            Assert.NotNull(price);
            Assert.Equal("Sample", price.ItemName);
            Assert.NotNull(price.Price);
            Assert.Equal(12 * 10_000L + 12 * 100L + 12, price.Price.Copper);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class FixedCapture : IScreenshotProvider
    {
        private readonly AutomationFrame _frame;

        public FixedCapture(AutomationFrame frame) => _frame = frame;

        public Task<AutomationFrame> CaptureAsync(CaptureRegion region, CancellationToken cancellationToken = default) =>
            Task.FromResult(_frame);
    }

    private sealed class FixedOcr : IOcrReader
    {
        private readonly string _text;

        public FixedOcr(string text) => _text = text;

        public Task<OcrResult?> ReadAsync(AutomationFrame frame, CaptureRegion region, CancellationToken cancellationToken = default) =>
            Task.FromResult<OcrResult?>(new OcrResult(_text, 1m));
    }

    private static byte[] BottomUp24BitBmp(int width, int height, byte[] bgr)
    {
        var rowStride = (width * 3 + 3) & ~3;
        var bytes = new byte[54 + rowStride * height];
        bytes[0] = (byte)'B';
        bytes[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(2), bytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(10), 54);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18), width);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(22), height);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(26), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(28), 24);
        for (var y = 0; y < height; y++)
        {
            var sourceRow = height - 1 - y;
            for (var x = 0; x < width; x++)
            {
                var source = (sourceRow * width + x) * 3;
                var target = 54 + y * rowStride + x * 3;
                bytes[target] = bgr[source];
                bytes[target + 1] = bgr[source + 1];
                bytes[target + 2] = bgr[source + 2];
            }
        }

        return bytes;
    }
}
