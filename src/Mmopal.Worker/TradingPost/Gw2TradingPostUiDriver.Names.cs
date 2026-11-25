using Microsoft.Extensions.Logging;
using Mmopal.Contracts;
using Mmopal.Native;
using Mmopal.Worker.Vision;

namespace Mmopal.Worker;

/// <summary>Finding an item's name by drawing it (<see cref="DrawnNameReader"/>), and counting how well the screen was read.</summary>
public sealed partial class Gw2TradingPostUiDriver
{
    // Measured on 45 frames of 20-24 Nov: the pen is always here, for white, yellow and orange names alike.
    private static readonly NameField FirstRowName = new(392.5, 261.5, 11.9);
    private static readonly NameField SearchBoxName = new(75.75, 188.25, 13.9, DarkestChannel: true);
    private const int HardNamesPerSession = 40;

    private readonly Dictionary<string, int> _readings = new(StringComparer.Ordinal);
    private readonly HashSet<string> _hardNamesSaved = new(StringComparer.OrdinalIgnoreCase);
    private int? _lastDrawnRow;

    /// <summary>How often each thing on screen was read how, for example "name: drawn" → 960.</summary>
    public IReadOnlyDictionary<string, int> Readings => _readings;

    private void CountReading(string what, string how)
    {
        var key = $"{what}: {how}";
        _readings[key] = _readings.GetValueOrDefault(key) + 1;
    }

    /// <summary>The result row (0 = first) that shows exactly this name, or null.</summary>
    private async Task<int?> FindDrawnRowAsync(string itemName, int rows, CancellationToken cancellationToken)
    {
        var frame = await _screenshots.CaptureAsync(CaptureRegion.FullScreen1024x768, cancellationToken).ConfigureAwait(false);
        for (var row = 0; row < rows; row++)
        {
            // 61.5 px apart where measured (23 Nov); the click strides are tried too.
            double[] strides = row == 0 ? [0] : [61.5, SellResultRowStride, SearchResultRowStride];
            if (strides.Any(stride => DrawnNameReader.Compare(frame, FirstRowName with { Top = FirstRowName.Top + row * stride }, itemName.Trim()).IsProof))
            {
                return row;
            }
        }

        return null;
    }

    private async Task<bool> SearchBoxShowsAsync(string itemName, CancellationToken cancellationToken)
    {
        var frame = await _screenshots.CaptureAsync(CaptureRegion.FullScreen1024x768, cancellationToken).ConfigureAwait(false);
        return DrawnNameReader.Compare(frame, SearchBoxName, itemName.Trim()).IsProof;
    }

    /// <summary>Rows to open, the one that shows the name first; its title is checked all the same.</summary>
    private IEnumerable<int> RowsToOpen() =>
        _lastDrawnRow is { } drawn
            ? Enumerable.Range(0, MaxSearchRowsToTry).OrderBy(row => row == drawn ? 0 : 1)
            : Enumerable.Range(0, MaxSearchRowsToTry);

    /// <summary>
    /// Keeps the first result row of a name that could not be found, named after it: put into Fixtures/name-rows it
    /// becomes a test.
    /// </summary>
    private async Task SaveHardNameAsync(string itemName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_diagnosticsDirectory) || _hardNamesSaved.Count >= HardNamesPerSession || !_hardNamesSaved.Add(itemName.Trim()))
        {
            return;
        }

        try
        {
            var frame = await _screenshots.CaptureAsync(CaptureRegion.FullScreen1024x768, cancellationToken).ConfigureAwait(false);
            if (FrameVision.Crop(frame, HardNameRegion) is not { } crop)
            {
                return;
            }

            var directory = Path.Combine(_diagnosticsDirectory, "names");
            Directory.CreateDirectory(directory);
            var name = string.Concat(itemName.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            FrameImageFile.TrySavePng(crop, Path.Combine(directory, name + ".png"));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogDebug(exception, "Could not save the row of a name that was not found.");
        }
    }

    // The crop of Fixtures/name-rows.
    private static readonly CaptureRegion HardNameRegion = new(386, 256, 330, 28);
}
