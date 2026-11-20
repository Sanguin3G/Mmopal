using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Mmopal.Contracts;
using Mmopal.Gw2;
using Mmopal.Infrastructure;
using Mmopal.Worker;

namespace Mmopal.Domain.Tests;

public sealed class WorkerHealthTests
{
    [Fact]
    public async Task Trading_health_uses_the_names_the_session_card_reads()
    {
        var root = Path.Combine(Path.GetTempPath(), "mmopal-health-" + Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(root, "checkpoint.db");
        var fixturePath = Path.Combine(root, "observation.jsonl");
        Directory.CreateDirectory(root);
        var store = new CheckpointStore(databasePath);
        await store.InitializeAsync();
        var runtime = new WorkerRuntime(store, NullLogger.Instance);
        var health = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        string? error = null;
        runtime.EventRaised += (_, workerEvent) =>
        {
            if (workerEvent.Type == WorkerEventTypes.Health)
            {
                health.TrySetResult(workerEvent.Payload);
            }
            else if (workerEvent.Type == WorkerEventTypes.Error)
            {
                error = workerEvent.Payload;
            }
        };

        try
        {
            var observedAt = DateTimeOffset.UtcNow;
            var observation = new TradingPostObservation(
                "fixture",
                observedAt,
                new MarketSnapshot(
                    new[]
                    {
                        new Transaction(TransactionType.Buy, "Glob of Ectoplasm", 100, 2, observedAt, 120, 150),
                        new Transaction(TransactionType.Sell, "Glob of Ectoplasm", 200, 1, observedAt, 200, 150)
                    },
                    new AccountBalance(1_000),
                    observedAt,
                    "fixture"),
                TradingPostObservationStatus.Ready,
                new TradingPostUiSnapshot(true, true, new Dictionary<string, bool>(), observedAt),
                new VisiblePriceObservation("Glob of Ectoplasm", new PriceQuote(4321, PriceSource.Ocr, observedAt)),
                new Dictionary<string, long> { ["api"] = 3 },
                new Dictionary<string, string>());
            await using (var recorder = new FileTradingObservationRecorder(fixturePath))
            {
                await recorder.RecordAsync(new TradingPostObservationReadResult(observation, null));
            }

            await runtime.HandleAsync(new WorkerCommand(
                WorkerCommandTypes.Start,
                Bot: BotKind.TradingPost,
                Mode: RunMode.DryRun,
                Profile: "balanced",
                Payload: JsonSerializer.Serialize(
                    new TradingReplayRequest(FixturePath: fixturePath),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web))));

            var completed = await Task.WhenAny(health.Task, Task.Delay(TimeSpan.FromSeconds(15)));
            Assert.True(health.Task.IsCompleted, error ?? "The worker did not report session health.");
            Assert.Same(health.Task, completed);
            var payload = await health.Task;

            using var document = JsonDocument.Parse(payload);
            var rootElement = document.RootElement;
            Assert.True(rootElement.GetProperty("ui").GetProperty("tradingPostOpen").GetBoolean());
            Assert.True(rootElement.GetProperty("ui").GetProperty("itemWindowOpen").GetBoolean());
            Assert.Equal(4321, rootElement.GetProperty("visiblePrice").GetProperty("price").GetProperty("copper").GetInt64());
            Assert.Equal(3, rootElement.GetProperty("timings").GetProperty("api").GetInt64());
            Assert.Equal(0, rootElement.GetProperty("actions").GetInt32());
            Assert.Equal(
                "1 buy and 1 sell. 1 buy is behind the top bid.",
                rootElement.GetProperty("orderBook").GetString());
        }
        finally
        {
            await runtime.StopAsync(CancellationToken.None);
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Worker_loads_candidates_from_file_and_applies_trading_limits()
    {
        var root = Path.Combine(Path.GetTempPath(), "mmopal-candidates-run-" + Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(root, "checkpoint.db");
        var candidatesPath = Path.Combine(root, "candidates.json");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(candidatesPath, """
            [
              {"itemName":"Accepted Item","quantity":1,"buyPriceCopper":10000,"sellPriceCopper":20000},
              {"itemName":"Too Expensive","quantity":1,"buyPriceCopper":50000,"sellPriceCopper":100000}
            ]
            """);

        var store = new CheckpointStore(databasePath);
        await store.InitializeAsync();
        var runtime = new WorkerRuntime(store, NullLogger.Instance);
        var health = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        string? error = null;
        runtime.EventRaised += (_, workerEvent) =>
        {
            if (workerEvent.Type == WorkerEventTypes.Health) health.TrySetResult(workerEvent.Payload);
            else if (workerEvent.Type == WorkerEventTypes.Error) error = workerEvent.Payload;
        };

        try
        {
            await runtime.HandleAsync(new WorkerCommand(
                WorkerCommandTypes.Start,
                Bot: BotKind.TradingPost,
                Mode: RunMode.DryRun,
                Profile: "balanced",
                Payload: JsonSerializer.Serialize(
                    new TradingReplayRequest(
                        Balance: new AccountBalance(1_000_000),
                        Policy: new ProfitabilityPolicy(MinimumProfitPercentage: 50, MaximumBuyPriceCopper: 20_000),
                        CandidatesPath: candidatesPath),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web))));

            var completed = await Task.WhenAny(health.Task, Task.Delay(TimeSpan.FromSeconds(15)));
            Assert.True(health.Task.IsCompleted, error ?? "The worker did not report session health.");
            Assert.Same(health.Task, completed);

            using var doc = JsonDocument.Parse(await health.Task);
            Assert.Equal(2, doc.RootElement.GetProperty("decisions").GetInt32());
            Assert.Equal(1, doc.RootElement.GetProperty("actions").GetInt32());
        }
        finally
        {
            await runtime.StopAsync(CancellationToken.None);
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Pause_during_an_active_run_is_not_overwritten_when_the_cycle_completes()
    {
        var root = Path.Combine(Path.GetTempPath(), "mmopal-pause-" + Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(root, "checkpoint.db");
        Directory.CreateDirectory(root);
        var store = new CheckpointStore(databasePath);
        await store.InitializeAsync();
        var runtime = new WorkerRuntime(store, NullLogger.Instance);
        var firstHealth = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.EventRaised += (_, workerEvent) =>
        {
            if (workerEvent.Type == WorkerEventTypes.Health)
            {
                firstHealth.TrySetResult(workerEvent.Payload);
            }
        };

        try
        {
            await runtime.HandleAsync(new WorkerCommand(
                WorkerCommandTypes.Start,
                Bot: BotKind.TradingPost,
                Mode: RunMode.DryRun,
                Profile: "fast",
                Payload: JsonSerializer.Serialize(
                    new TradingReplayRequest(Balance: new AccountBalance(10_000)),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web))));

            await runtime.HandleAsync(new WorkerCommand(WorkerCommandTypes.Pause));
            await Task.WhenAny(firstHealth.Task, Task.Delay(TimeSpan.FromSeconds(5)));

            Assert.Equal(WorkerState.Paused, runtime.Status.State);

            await runtime.HandleAsync(new WorkerCommand(WorkerCommandTypes.Resume));
            Assert.NotEqual(WorkerState.Paused, runtime.Status.State);
        }
        finally
        {
            await runtime.StopAsync(CancellationToken.None);
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
