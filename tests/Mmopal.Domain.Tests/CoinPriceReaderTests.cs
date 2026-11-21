using Mmopal.Contracts;
using Mmopal.Worker.Vision;
using OpenCvSharp;

namespace Mmopal.Domain.Tests;

/// <summary>
/// Reads real GW2 Trading Post dialogs (cropped from captured 1024x768 frames, see Fixtures/coin-frames) by digit
/// shape. The embedded templates were cut from nikas-coat and pyre-dye plus the berserkers-axe Total line; the
/// berserkers-axe list rows were never seen.
/// </summary>
public sealed class CoinPriceReaderTests
{
    private static readonly CaptureRegion Total = new(382, 330, 250, 44);

    // Only the embedded templates: shapes learned on this machine (%LOCALAPPDATA%\Mmopal) must not change results.
    public CoinPriceReaderTests()
    {
        CoinPriceReader.LearnedTemplatesPath = Path.Combine(Path.GetTempPath(), "mmopal-glyphs-tests-none.json");
        CoinPriceReader.ReloadTemplates();
    }
    private static readonly CaptureRegion BuyerPrices = new(403, 505, 112, 190);
    private static readonly CaptureRegion SellerPrices = new(688, 505, 112, 190);

    /// <summary>Puts a dialog crop back at its place (240, 320) in a black 1024x768 BGRA frame.</summary>
    private static AutomationFrame Frame(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "coin-frames", name + ".png");
        using var crop = Cv2.ImRead(path, ImreadModes.Color);
        using var canvas = new Mat(768, 1024, MatType.CV_8UC3, Scalar.Black);
        crop.CopyTo(canvas[new Rect(240, 320, crop.Width, crop.Height)]);
        using var bgra = new Mat();
        Cv2.CvtColor(canvas, bgra, ColorConversionCodes.BGR2BGRA);
        var bytes = new byte[1024 * 768 * 4];
        System.Runtime.InteropServices.Marshal.Copy(bgra.Data, bytes, 0, bytes.Length);
        return new AutomationFrame(DateTimeOffset.UtcNow, CaptureRegion.FullScreen1024x768, bytes, "Bgra32", 1024 * 4);
    }

    [Theory]
    [InlineData("nikas-coat", 4_734L)]        // 0g 47s 34c
    [InlineData("pyre-dye", 11_798L)]         // 1g 17s 98c
    [InlineData("berserkers-axe", 22_662L)]   // 2g 26s 62c
    public void Reads_the_dialog_total(string frame, long expected)
    {
        Assert.Equal(expected, CoinPriceReader.ReadFirst(Frame(frame), Total)?.Copper);
    }

    [Fact]
    public void Reads_every_list_row_including_rows_never_trained_on()
    {
        var axe = Frame("berserkers-axe");

        Assert.Equal(
            [11_336L, 11_335, 11_332, 11_331, 11_329],
            CoinPriceReader.ReadAll(axe, BuyerPrices).Select(r => r.Copper).Take(5));
        Assert.Equal(
            [21_086L, 21_087, 21_088, 21_090, 21_092, 21_792],
            CoinPriceReader.ReadAll(axe, SellerPrices).Select(r => r.Copper));
        Assert.Equal(
            [6_995L, 5_899, 5_898, 5_795, 5_794, 5_792],
            CoinPriceReader.ReadAll(Frame("pyre-dye"), BuyerPrices).Select(r => r.Copper));
    }

    [Fact]
    public void Top_row_is_the_first_reading()
    {
        Assert.Equal(4_350, CoinPriceReader.ReadFirst(Frame("nikas-coat"), SellerPrices)?.Copper); // 43s 50c
        Assert.Equal(12_041, CoinPriceReader.ReadFirst(Frame("pyre-dye"), SellerPrices)?.Copper);  // 1g 20s 41c
    }

    [Fact]
    public void Nothing_to_read_gives_null_not_a_guess()
    {
        var black = new AutomationFrame(DateTimeOffset.UtcNow, CaptureRegion.FullScreen1024x768, new byte[1024 * 768 * 4], "Bgra32", 1024 * 4);
        Assert.Null(CoinPriceReader.ReadFirst(black, Total));
        // The "Total Price:" label alone is letters, not a price.
        Assert.Null(CoinPriceReader.ReadFirst(Frame("nikas-coat"), new CaptureRegion(260, 330, 120, 44)));
    }

    [Theory]
    [InlineData(22_662L, 3, new[] { "2", "26", "62" })]
    [InlineData(4_734L, 3, new[] { "0", "47", "34" })]    // the Total line shows a zero gold
    [InlineData(4_734L, 2, new[] { "47", "34" })]         // list rows leave it out
    [InlineData(12_000L, 3, new[] { "1", "20", "0" })]
    public void Expected_digit_groups_match_how_GW2_lays_out_coins(long copper, int groups, string[] expected)
    {
        Assert.Equal(expected, CoinPriceReader.ExpectedGroupDigits(copper, groups));
    }

    [Fact]
    public void A_nonzero_gold_cannot_be_left_out()
    {
        Assert.Null(CoinPriceReader.ExpectedGroupDigits(22_662, 2));
    }

    [Fact]
    public void Learning_is_skipped_when_every_digit_is_already_known()
    {
        var learnedPath = CoinPriceReader.LearnedTemplatesPath;
        Assert.Equal(0, CoinPriceReader.LearnFromConfirmed(Frame("pyre-dye"), Total, 11_798));
        // A total that disagrees with confidently read digits is never learned from.
        Assert.Equal(0, CoinPriceReader.LearnFromConfirmed(Frame("pyre-dye"), Total, 11_799));
        Assert.False(File.Exists(learnedPath));
    }
}
