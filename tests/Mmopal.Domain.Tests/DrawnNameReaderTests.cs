using Mmopal.Contracts;
using Mmopal.Worker.Vision;
using OpenCvSharp;

namespace Mmopal.Domain.Tests;

/// <summary>
/// Real result rows Windows OCR could not read (20-24 Nov), one file per item name in Fixtures/name-rows. A row saved by
/// Mmopal (screenshots\names) becomes a test by being put there.
/// </summary>
public sealed class DrawnNameReaderTests
{
    private static readonly NameField Row = new(392.5, 261.5, 11.9);

    private static string[] Rows() =>
        Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures", "name-rows"), "*.png").Select(file => Path.GetFileNameWithoutExtension(file)!).ToArray();

    public static IEnumerable<object[]> Names() => Rows().Select(name => new object[] { name });

    private static AutomationFrame Frame(string name)
    {
        using var crop = Cv2.ImDecode(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "name-rows", name + ".png")), ImreadModes.Color);
        using var canvas = new Mat(768, 1024, MatType.CV_8UC3, Scalar.Black);
        crop.CopyTo(canvas[new Rect(386, 256, crop.Width, crop.Height)]);
        using var bgra = new Mat();
        Cv2.CvtColor(canvas, bgra, ColorConversionCodes.BGR2BGRA);
        var bytes = new byte[1024 * 768 * 4];
        System.Runtime.InteropServices.Marshal.Copy(bgra.Data, bytes, 0, bytes.Length);
        return new AutomationFrame(DateTimeOffset.UtcNow, CaptureRegion.FullScreen1024x768, bytes, "Bgra32", 1024 * 4);
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void The_name_of_the_row_is_proven(string name)
    {
        var match = DrawnNameReader.Compare(Frame(name), Row, name);

        Assert.True(match.IsProof, match.ToString());
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void No_other_name_passes_for_it(string name)
    {
        var frame = Frame(name);
        // Every other row's name, and this one with a letter or digit changed, cut or added.
        var others = Rows()
            .Concat(name.Select((c, i) => char.IsAsciiDigit(c) ? name[..i] + (char)('0' + (c - '0' + 1) % 10) + name[(i + 1)..] : name))
            .Concat([name[..^1], name + "s", name + " 2", "Superior " + name, name.Replace("Dye", "Dry", StringComparison.Ordinal)])
            .Where(other => other != name);

        Assert.All(others, other => Assert.False(DrawnNameReader.Compare(frame, Row, other).IsProof, $"'{other}' passed for '{name}'"));
    }

    [Theory]
    [InlineData("Swim-Speed Infusion +16", "Swim-Speed Infusion +15")]
    [InlineData("Swim-Speed Infusion +16", "Swim-Speed Infusion +18")]
    [InlineData("Swim-Speed Infusion +16", "Swim-Speed Infusion +1")]
    [InlineData("15 Slot Linen Bag", "18 Slot Linen Bag")]
    [InlineData("18 Slot Thick Leather Pack", "15 Slot Thick Leather Pack")]
    [InlineData("Superior Rune of the Renegade", "Superior Rune of the Revenant")]
    [InlineData("Superior Rune of the Citadel", "Relic of the Citadel")]
    [InlineData("Reclaimed Torch", "Chained Torch")]
    [InlineData("Flare Dye", "Flame Dye")]
    [InlineData("Ghost Dye", "Ghost Dyer")]
    public void Names_that_look_alike_are_told_apart(string shown, string other) =>
        Assert.False(DrawnNameReader.Compare(Frame(shown), Row, other).IsProof);

    [Fact]
    public void A_row_somewhere_else_or_no_row_proves_nothing()
    {
        var frame = Frame("Pyre Dye");

        Assert.False(DrawnNameReader.Compare(frame, Row with { Top = Row.Top + 58 }, "Pyre Dye").IsProof);
        Assert.False(DrawnNameReader.Compare(frame, new NameField(2, 2, 11.9), "Pyre Dye").IsProof);
        Assert.False(DrawnNameReader.Compare(new AutomationFrame(DateTimeOffset.UtcNow, CaptureRegion.FullScreen1024x768, new byte[4], "Bgra32", 4), Row, "Pyre Dye").IsProof);
        Assert.False(DrawnNameReader.Compare(frame, Row, " ").IsProof);
    }
}
