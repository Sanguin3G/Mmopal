using System.Globalization;
using Mmopal.Contracts;

namespace Mmopal.Native;

/// <summary>
/// Reads the Trading Post from a captured frame. It does not click or type.
/// The price rectangles are measured from the detected price template anchor.
/// </summary>
public sealed class FrameTradingPostUiProbe : ITradingPostUiProbe
{
    private readonly IScreenshotProvider _screenshots;
    private readonly ITemplateMatcher _templates;
    private readonly IOcrReader _ocr;

    public FrameTradingPostUiProbe(IScreenshotProvider screenshots, ITemplateMatcher templates, IOcrReader ocr)
    {
        _screenshots = screenshots ?? throw new ArgumentNullException(nameof(screenshots));
        _templates = templates ?? throw new ArgumentNullException(nameof(templates));
        _ocr = ocr ?? throw new ArgumentNullException(nameof(ocr));
    }

    public async Task<TradingPostUiSnapshot> ProbeAsync(
        TradingPostUiProbeOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var frame = await _screenshots.CaptureAsync(options.EffectiveCaptureRegion, cancellationToken);
        var region = options.EffectiveCaptureRegion;
        var detected = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            ["tradingpost_tab"] = await FindAsync(frame, options, "tradingpost_tab.png", new CaptureRegion(region.X, region.Y, region.Width, Math.Min(region.Height, 400)), cancellationToken),
            ["currentBuyers"] = await FindAsync(frame, options, "currentBuyers.png", region, cancellationToken),
            ["currentSellers"] = await FindAsync(frame, options, "currentSellers.png", region, cancellationToken)
        };
        return new TradingPostUiSnapshot(
            detected["tradingpost_tab"],
            detected["currentBuyers"] || detected["currentSellers"],
            detected,
            DateTimeOffset.UtcNow);
    }

    public async Task<VisiblePriceObservation?> ReadVisiblePriceAsync(
        TradingPostUiProbeOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var imagePath = ResolveTemplate(options.TemplateRoot, "price_2.png");
        if (!File.Exists(imagePath)) return new VisiblePriceObservation(null, null, $"Price template was not found: {imagePath}");

        var frame = await _screenshots.CaptureAsync(options.EffectiveCaptureRegion, cancellationToken);
        var match = await _templates.FindAsync(
            frame,
            new TemplateDefinition("price_2", imagePath, options.EffectiveCaptureRegion, Math.Min(options.MatchScore, 0.75m)),
            cancellationToken);
        if (match is null) return new VisiblePriceObservation(null, null, "The visible price control was not detected.");

        var anchorX = match.X + 252;
        var anchorY = match.Y - 11;
        var copper = await ReadComponentAsync(frame, anchorX, anchorY, -15, 40, cancellationToken);
        var silver = await ReadComponentAsync(frame, anchorX, anchorY, -95, 40, cancellationToken);
        var gold = await ReadComponentAsync(frame, anchorX, anchorY, -200, 70, cancellationToken);
        if (copper is null || silver is null || gold is null)
        {
            return new VisiblePriceObservation(null, null, "OCR did not return all three currency components.");
        }

        if (silver is < 0 or > 99 || copper is < 0 or > 99 || gold < 0)
        {
            return new VisiblePriceObservation(null, null, "OCR currency components were outside GW2 bounds.");
        }

        try
        {
            var totalCopper = checked(gold.Value * 10_000L + silver.Value * 100L + copper.Value);
            return new VisiblePriceObservation(
                options.VisibleItemName,
                new PriceQuote(totalCopper, PriceSource.Ocr, DateTimeOffset.UtcNow, 0.8m));
        }
        catch (OverflowException)
        {
            return new VisiblePriceObservation(null, null, "OCR price overflowed the supported currency range.");
        }
    }

    private async Task<bool> FindAsync(
        AutomationFrame frame,
        TradingPostUiProbeOptions options,
        string fileName,
        CaptureRegion region,
        CancellationToken cancellationToken)
    {
        var path = ResolveTemplate(options.TemplateRoot, fileName);
        if (!File.Exists(path)) return false;
        var match = await _templates.FindAsync(
            frame,
            new TemplateDefinition(fileName, path, region, options.MatchScore),
            cancellationToken);
        return match is not null;
    }

    private async Task<long?> ReadComponentAsync(
        AutomationFrame frame,
        int anchorX,
        int anchorY,
        int xOffset,
        int width,
        CancellationToken cancellationToken)
    {
        var region = new CaptureRegion(Math.Max(0, anchorX + xOffset), Math.Max(0, anchorY - 18), width, 36);
        var result = await _ocr.ReadAsync(frame, region, cancellationToken);
        if (result is null || string.IsNullOrWhiteSpace(result.Text)) return null;
        var digits = new string(result.Text.Where(char.IsDigit).ToArray());
        return long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    private static string ResolveTemplate(string root, string fileName) =>
        Path.Combine(Path.GetFullPath(Environment.ExpandEnvironmentVariables(root)), fileName);
}
