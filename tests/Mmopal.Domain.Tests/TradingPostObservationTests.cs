using Mmopal.Contracts;
using Mmopal.Gw2;

namespace Mmopal.Domain.Tests;

public sealed class TradingPostObservationTests
{
    [Fact]
    public async Task Replay_reader_rebases_old_fixture_time_and_repeats_last_observation()
    {
        var old = DateTimeOffset.UtcNow.AddHours(-4);
        var observation = CreateObservation(old, TradingPostObservationStatus.Ready);
        var reader = new ReplayTradingPostObservationReader(new[] { observation });

        var first = await reader.ReadAsync(new TradingPostReadRequest(string.Empty, CaptureFrame: false, ReadVisiblePrice: false));
        var second = await reader.ReadAsync(new TradingPostReadRequest(string.Empty, CaptureFrame: false, ReadVisiblePrice: false));

        Assert.True(first.Observation.ObservedAt > old);
        Assert.True(first.Observation.Market.ObservedAt > old);
        Assert.Equal(first.Observation.Market.Transactions, second.Observation.Market.Transactions);
        Assert.Equal(TradingPostObservationStatus.Ready, second.Observation.Status);
    }

    [Fact]
    public async Task Observation_market_reader_removes_balance_when_ui_is_not_ready()
    {
        var observation = CreateObservation(DateTimeOffset.UtcNow, TradingPostObservationStatus.NotReady);
        var reader = new TradingPostMarketReader(
            new StaticObservationReader(observation),
            new TradingPostReadRequest(string.Empty, CaptureFrame: false, ReadVisiblePrice: false));

        var market = await reader.ReadAsync();

        Assert.Null(market.Balance);
        Assert.Equal("fixture:safe-stop", market.Source);
        Assert.Equal(TradingPostObservationStatus.NotReady, reader.LastRead!.Observation.Status);
    }

    [Fact]
    public async Task Screen_observation_reader_combines_api_data_ui_probe_and_visible_price()
    {
        var observedAt = DateTimeOffset.UtcNow;
        var reader = new ScreenTradingPostObservationReader(
            new StubDataProvider(
                [new Transaction(TransactionType.Buy, "Glob of Ectoplasm", 100, 2, observedAt, 120, 90)],
                new AccountBalance(50_000)),
            new StubUiProbe(
                new TradingPostUiSnapshot(true, true, new Dictionary<string, bool> { ["tradingpost_tab"] = true }, observedAt),
                new VisiblePriceObservation("Glob of Ectoplasm", new PriceQuote(12_345, PriceSource.Ocr, observedAt))),
            new StubScreenshotProvider());

        var result = await reader.ReadAsync(new TradingPostReadRequest(
            string.Empty,
            CaptureFrame: true,
            ReadVisiblePrice: true,
            VisibleItemName: "Glob of Ectoplasm"));

        Assert.Equal("screen", result.Observation.Source);
        Assert.Equal(TradingPostObservationStatus.Ready, result.Observation.Status);
        Assert.True(result.Observation.Ui.TradingPostOpen);
        Assert.True(result.Observation.Ui.ItemWindowOpen);
        Assert.Equal(12_345, result.Observation.VisiblePrice?.Price?.Copper);
        Assert.Equal(50_000, result.Observation.Market.Balance?.Copper);
        Assert.Single(result.Observation.Market.Transactions);
        Assert.NotNull(result.Frame);
    }

    [Fact]
    public async Task Fixture_recorder_round_trips_observation_and_frame_artifact()
    {
        var fixturePath = Path.Combine(Path.GetTempPath(), "mmopal-observation-" + Guid.NewGuid().ToString("N") + ".jsonl");
        var frameDirectory = Path.Combine(Path.GetDirectoryName(fixturePath)!, Path.GetFileNameWithoutExtension(fixturePath) + ".frames");
        try
        {
            var observation = CreateObservation(DateTimeOffset.UtcNow, TradingPostObservationStatus.Ready);
            await using (var recorder = new FileTradingObservationRecorder(fixturePath))
            {
                await recorder.RecordAsync(new TradingPostObservationReadResult(
                    observation,
                    new AutomationFrame(DateTimeOffset.UtcNow, CaptureRegion.FullScreen1024x768, new byte[] { 1, 2, 3, 4 })));
            }

            var replay = await ReplayTradingPostObservationReader.LoadAsync(fixturePath);
            var result = await replay.ReadAsync(new TradingPostReadRequest(string.Empty, CaptureFrame: false, ReadVisiblePrice: false));

            Assert.Equal(observation.Market.Transactions, result.Observation.Market.Transactions);
            Assert.Equal(TradingPostObservationStatus.Ready, result.Observation.Status);
            Assert.True(File.Exists(fixturePath));
            Assert.True(Directory.Exists(frameDirectory));
            Assert.Single(Directory.EnumerateFiles(frameDirectory, "*.bgra"));
        }
        finally
        {
            File.Delete(fixturePath);
            if (Directory.Exists(frameDirectory)) Directory.Delete(frameDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Replay_reader_handles_long_runs_without_recursive_growth()
    {
        var reader = new ReplayTradingPostObservationReader(new[] { CreateObservation(DateTimeOffset.UtcNow, TradingPostObservationStatus.Ready) });
        for (var index = 0; index < 10_000; index++)
        {
            var result = await reader.ReadAsync(new TradingPostReadRequest(string.Empty, CaptureFrame: false, ReadVisiblePrice: false));
            Assert.Equal(TradingPostObservationStatus.Ready, result.Observation.Status);
        }
    }

    private static TradingPostObservation CreateObservation(DateTimeOffset observedAt, TradingPostObservationStatus status) =>
        new(
            "fixture",
            observedAt,
            new MarketSnapshot(
                new[] { new Transaction(TransactionType.Buy, "Test Item", 100, 1, observedAt, null, null) },
                new AccountBalance(100_000),
                observedAt,
                "fixture"),
            status,
            new TradingPostUiSnapshot(status == TradingPostObservationStatus.Ready, false, new Dictionary<string, bool>(), observedAt),
            null,
            new Dictionary<string, long>(),
            new Dictionary<string, string>());

    private sealed class StaticObservationReader : ITradingPostObservationReader
    {
        private readonly TradingPostObservation _observation;

        public StaticObservationReader(TradingPostObservation observation) => _observation = observation;

        public Task<TradingPostObservationReadResult> ReadAsync(TradingPostReadRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new TradingPostObservationReadResult(_observation, null));
        }
    }

    private sealed class StubDataProvider : IGw2DataProvider
    {
        private readonly IReadOnlyList<Transaction> _transactions;
        private readonly AccountBalance? _balance;

        public StubDataProvider(IReadOnlyList<Transaction> transactions, AccountBalance? balance)
        {
            _transactions = transactions;
            _balance = balance;
        }

        public Task<IReadOnlyList<Transaction>> GetTransactionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_transactions);

        public Task<AccountBalance?> GetBalanceAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_balance);
    }

    private sealed class StubUiProbe : ITradingPostUiProbe
    {
        private readonly TradingPostUiSnapshot _snapshot;
        private readonly VisiblePriceObservation? _visiblePrice;

        public StubUiProbe(TradingPostUiSnapshot snapshot, VisiblePriceObservation? visiblePrice)
        {
            _snapshot = snapshot;
            _visiblePrice = visiblePrice;
        }

        public Task<TradingPostUiSnapshot> ProbeAsync(TradingPostUiProbeOptions options, CancellationToken cancellationToken = default) =>
            Task.FromResult(_snapshot);

        public Task<VisiblePriceObservation?> ReadVisiblePriceAsync(TradingPostUiProbeOptions options, CancellationToken cancellationToken = default) =>
            Task.FromResult(_visiblePrice);
    }

    private sealed class StubScreenshotProvider : IScreenshotProvider
    {
        public Task<AutomationFrame> CaptureAsync(CaptureRegion region, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AutomationFrame(DateTimeOffset.UtcNow, region, new byte[4 * 4], "Bgra32", 4));
    }
}
