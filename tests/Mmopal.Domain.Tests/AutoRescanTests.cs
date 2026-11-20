using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Mmopal.Contracts;
using Mmopal.Gw2;
using Mmopal.Infrastructure;
using Mmopal.Worker;

namespace Mmopal.Domain.Tests;

public sealed class AutoRescanTests
{
    [Fact]
    public async Task Long_session_rescans_the_market_and_saves_the_fresh_list()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "mmopal-rescan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var store = new CheckpointStore(Path.Combine(tempRoot, "mmopal.db"));
            await store.InitializeAsync();
            var listPath = Path.Combine(tempRoot, "scanned-candidates.json");
            OperatorListsStore.SaveShoppingList(listPath, [new ItemCandidate("Old Dead Spread", Quantity: 2, BuyPriceCopper: 3_872, SellPriceCopper: 7_813)]);

            // Every clock read advances an hour, so the 3-hour re-scan interval elapses within the first cycles.
            var now = new DateTimeOffset(2026, 11, 20, 15, 0, 0, TimeSpan.Zero);
            var clockLock = new object();
            DateTimeOffset Clock()
            {
                lock (clockLock)
                {
                    now = now.AddHours(1);
                    return now;
                }
            }

            var scanner = new FakeScanner(Enumerable.Range(1, 6)
                .Select(i => new MarketScanItem(100 + i, $"Busy Item {i}", "Rare", 10_000, 13_000, 1_050, 10.5m, 900, 700, 2))
                .ToArray());
            var runtime = new WorkerRuntime(store, NullLogger.Instance, utcNow: Clock, marketScanner: scanner);
            var payload = JsonSerializer.Serialize(
                new TradingReplayRequest(
                    Source: "replay",
                    CandidatesPath: listPath,
                    ClientSettings: new Gw2ClientSettings(MaxSessionDurationMinutes: 0),
                    AutoRescanFilter: new MarketScanFilter(SortMode: MarketScanSortModes.GoldPerDay),
                    AutoRescanIntervalMinutes: 180),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));

            await runtime.HandleAsync(new WorkerCommand(WorkerCommandTypes.Start, "req-rescan", BotKind.TradingPost, RunMode.DryRun, "balanced", payload));
            await scanner.Called.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var saved = Array.Empty<ItemCandidate>();
            for (var i = 0; i < 50 && saved.Length != 6; i++)
            {
                await Task.Delay(100);
                saved = (await CandidatesLoader.LoadCandidatesAsync(listPath)).ToArray();
            }

            await runtime.StopAsync(CancellationToken.None);

            Assert.Equal(6, saved.Length);
            Assert.DoesNotContain(saved, c => c.ItemName == "Old Dead Spread");
            Assert.Equal(MarketScanSortModes.GoldPerDay, scanner.LastFilter!.SortMode);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    private sealed class FakeScanner(IReadOnlyList<MarketScanItem> results) : IGw2MarketScanner
    {
        public TaskCompletionSource Called { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public MarketScanFilter? LastFilter { get; private set; }

        public Task<IReadOnlyList<MarketScanItem>> ScanAsync(
            MarketScanFilter filter,
            IReadOnlyCollection<string>? blacklist = null,
            IProgress<MarketScanProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            LastFilter = filter;
            Called.TrySetResult();
            return Task.FromResult(results);
        }
    }
}
