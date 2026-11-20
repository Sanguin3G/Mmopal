using System.Diagnostics;
using System.Text.Json;
using Mmopal.Contracts;

namespace Mmopal.Gw2;

public sealed record TradingPostReadRequest(
    string TemplateRoot,
    CaptureRegion? CaptureRegion = null,
    int MaxWaitMilliseconds = 100,
    decimal MatchScore = 0.8m,
    bool CaptureFrame = true,
    bool ReadVisiblePrice = true,
    string? VisibleItemName = null)
{
    public CaptureRegion EffectiveCaptureRegion => CaptureRegion ?? Mmopal.Contracts.CaptureRegion.FullScreen1024x768;
}

public sealed record TradingPostObservationReadResult(
    TradingPostObservation Observation,
    AutomationFrame? Frame);

public interface ITradingPostObservationReader
{
    Task<TradingPostObservationReadResult> ReadAsync(
        TradingPostReadRequest request,
        CancellationToken cancellationToken = default);
}

public interface ITradingPostObservationRecorder
{
    Task RecordAsync(
        TradingPostObservationReadResult result,
        CancellationToken cancellationToken = default);
}

public sealed class ScreenTradingPostObservationReader : ITradingPostObservationReader
{
    private readonly IGw2DataProvider _dataProvider;
    private readonly ITradingPostUiProbe _uiProbe;
    private readonly IScreenshotProvider _screenshotProvider;
    private readonly string _source;

    public ScreenTradingPostObservationReader(
        IGw2DataProvider dataProvider,
        ITradingPostUiProbe uiProbe,
        IScreenshotProvider screenshotProvider,
        string source = "screen")
    {
        _dataProvider = dataProvider ?? throw new ArgumentNullException(nameof(dataProvider));
        _uiProbe = uiProbe ?? throw new ArgumentNullException(nameof(uiProbe));
        _screenshotProvider = screenshotProvider ?? throw new ArgumentNullException(nameof(screenshotProvider));
        _source = string.IsNullOrWhiteSpace(source) ? "screen" : source;
    }

    public async Task<TradingPostObservationReadResult> ReadAsync(
        TradingPostReadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var observedAt = DateTimeOffset.UtcNow;
        var timings = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var diagnostics = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var transactions = Array.Empty<Transaction>();
        AccountBalance? balance = null;
        Exception? transactionError = null;
        Exception? balanceError = null;

        var captureTask = request.CaptureFrame
            ? CaptureFrameAsync(request, timings, diagnostics, cancellationToken)
            : Task.FromResult<AutomationFrame?>(null);

        var apiTimer = Stopwatch.StartNew();
        try
        {
            transactions = (await _dataProvider.GetTransactionsAsync(cancellationToken)).ToArray();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            transactionError = exception;
            diagnostics["transactions"] = exception.Message;
        }

        try
        {
            balance = await _dataProvider.GetBalanceAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            balanceError = exception;
            diagnostics["balance"] = exception.Message;
        }

        apiTimer.Stop();
        timings["api"] = apiTimer.ElapsedMilliseconds;

        TradingPostUiSnapshot ui;
        var uiTimer = Stopwatch.StartNew();
        try
        {
            ui = await _uiProbe.ProbeAsync(
                new TradingPostUiProbeOptions(
                    request.TemplateRoot,
                    request.EffectiveCaptureRegion,
                    request.MaxWaitMilliseconds,
                    request.MatchScore,
                    request.ReadVisiblePrice,
                    request.VisibleItemName),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ui = new TradingPostUiSnapshot(
                false,
                false,
                new Dictionary<string, bool>(),
                DateTimeOffset.UtcNow);
            diagnostics["ui"] = exception.Message;
        }

        uiTimer.Stop();
        timings["ui"] = uiTimer.ElapsedMilliseconds;

        VisiblePriceObservation? visiblePrice = null;
        if (request.ReadVisiblePrice && ui.ItemWindowOpen)
        {
            var ocrTimer = Stopwatch.StartNew();
            try
            {
                visiblePrice = await _uiProbe.ReadVisiblePriceAsync(
                    new TradingPostUiProbeOptions(
                        request.TemplateRoot,
                        request.EffectiveCaptureRegion,
                        request.MaxWaitMilliseconds,
                        request.MatchScore,
                        request.ReadVisiblePrice,
                        request.VisibleItemName),
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                diagnostics["ocr"] = exception.Message;
                visiblePrice = new VisiblePriceObservation(null, null, exception.Message);
            }

            ocrTimer.Stop();
            timings["ocr"] = ocrTimer.ElapsedMilliseconds;
        }

        var frame = await captureTask;
        var market = new MarketSnapshot(transactions, balance, observedAt, _source);
        var status = transactionError is not null || balanceError is not null
            ? TradingPostObservationStatus.Partial
            : !ui.TradingPostOpen
                ? TradingPostObservationStatus.NotReady
                : TradingPostObservationStatus.Ready;
        var error = transactionError?.Message ?? balanceError?.Message;
        var observation = new TradingPostObservation(
            _source,
            observedAt,
            market,
            status,
            ui,
            visiblePrice,
            timings,
            diagnostics,
            error);
        return new TradingPostObservationReadResult(observation, frame);
    }

    private async Task<AutomationFrame?> CaptureFrameAsync(
        TradingPostReadRequest request,
        IDictionary<string, long> timings,
        IDictionary<string, string> diagnostics,
        CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            return await _screenshotProvider.CaptureAsync(request.EffectiveCaptureRegion, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            diagnostics["capture"] = exception.Message;
            return null;
        }
        finally
        {
            timer.Stop();
            timings["capture"] = timer.ElapsedMilliseconds;
        }
    }
}

public sealed class TradingPostMarketReader : IGw2MarketReader
{
    private readonly ITradingPostObservationReader _reader;
    private readonly TradingPostReadRequest _request;
    private readonly ITradingPostObservationRecorder? _recorder;

    public TradingPostMarketReader(
        ITradingPostObservationReader reader,
        TradingPostReadRequest request,
        ITradingPostObservationRecorder? recorder = null)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _request = request ?? throw new ArgumentNullException(nameof(request));
        _recorder = recorder;
    }

    public TradingPostObservationReadResult? LastRead { get; private set; }

    public async Task<MarketSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        var result = await _reader.ReadAsync(_request, cancellationToken);
        LastRead = result;
        if (_recorder is not null)
        {
            try
            {
                await _recorder.RecordAsync(result, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Recording is diagnostic and must not make observation unsafe.
            }
        }

        return result.Observation.Status == TradingPostObservationStatus.Ready
            ? result.Observation.Market
            : result.Observation.Market with { Balance = null, Source = result.Observation.Source + ":safe-stop" };
    }
}

public sealed record TradingObservationFixtureRecord(
    int SchemaVersion,
    TradingPostObservation Observation,
    string? FramePath = null);

public sealed class FileTradingObservationRecorder : ITradingPostObservationRecorder, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _fixturePath;
    private readonly string _frameDirectory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public FileTradingObservationRecorder(string fixturePath, string? frameDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(fixturePath)) throw new ArgumentException("A fixture path is required.", nameof(fixturePath));
        _fixturePath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(fixturePath));
        Directory.CreateDirectory(Path.GetDirectoryName(_fixturePath) ?? ".");
        _frameDirectory = string.IsNullOrWhiteSpace(frameDirectory)
            ? Path.Combine(Path.GetDirectoryName(_fixturePath) ?? ".", Path.GetFileNameWithoutExtension(_fixturePath) + ".frames")
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(frameDirectory));
    }

    public async Task RecordAsync(
        TradingPostObservationReadResult result,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            string? framePath = null;
            if (result.Frame is not null)
            {
                Directory.CreateDirectory(_frameDirectory);
                var frameName = $"frame-{result.Frame.CapturedAt:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.bgra";
                var absoluteFramePath = Path.Combine(_frameDirectory, frameName);
                await File.WriteAllBytesAsync(absoluteFramePath, result.Frame.Pixels, cancellationToken);
                framePath = Path.GetRelativePath(Path.GetDirectoryName(_fixturePath) ?? ".", absoluteFramePath);
            }

            var record = new TradingObservationFixtureRecord(1, result.Observation, framePath);
            await using var stream = new FileStream(_fixturePath, FileMode.Append, FileAccess.Write, FileShare.Read, 16 * 1024, useAsync: true);
            await JsonSerializer.SerializeAsync(stream, record, JsonOptions, cancellationToken);
            await stream.WriteAsync("\n"u8.ToArray(), cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed class ReplayTradingPostObservationReader : ITradingPostObservationReader
{
    private readonly IReadOnlyList<TradingPostObservation> _observations;
    private int _index;

    public ReplayTradingPostObservationReader(IReadOnlyList<TradingPostObservation> observations)
    {
        if (observations is null || observations.Count == 0) throw new ArgumentException("At least one observation is required.", nameof(observations));
        _observations = observations;
    }

    public static async Task<ReplayTradingPostObservationReader> LoadAsync(
        string fixturePath,
        CancellationToken cancellationToken = default)
    {
        var path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(fixturePath));
        if (!File.Exists(path)) throw new FileNotFoundException("Trading observation fixture was not found.", path);

        var observations = new List<TradingPostObservation>();
        await using var stream = File.OpenRead(path);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var record = JsonSerializer.Deserialize<TradingObservationFixtureRecord>(line, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (record is null || record.SchemaVersion != 1) throw new InvalidDataException("Unsupported trading observation fixture record.");
            observations.Add(record.Observation);
        }

        return new ReplayTradingPostObservationReader(observations);
    }

    public Task<TradingPostObservationReadResult> ReadAsync(
        TradingPostReadRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var index = Interlocked.Increment(ref _index) - 1;
        var original = _observations[Math.Min(index, _observations.Count - 1)];
        var now = DateTimeOffset.UtcNow;
        var market = original.Market with { ObservedAt = now };
        var visiblePrice = original.VisiblePrice is { } visible && visible.Price is { } price
            ? visible with { Price = price with { ObservedAt = now } }
            : original.VisiblePrice;
        var observation = original with
        {
            ObservedAt = now,
            Market = market,
            Ui = original.Ui with { ObservedAt = now },
            VisiblePrice = visiblePrice
        };
        return Task.FromResult(new TradingPostObservationReadResult(observation, null));
    }
}
