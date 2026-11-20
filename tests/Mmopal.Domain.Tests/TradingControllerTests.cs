using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Mmopal.Contracts;
using Mmopal.Gw2;
using Mmopal.Infrastructure;

namespace Mmopal.Domain.Tests;

public sealed class TradingControllerTests
{
    [Fact]
    public void Candidate_planner_returns_explanations_for_safe_non_actions()
    {
        var candidates = new[]
        {
            new ItemCandidate("Blacklisted Item", BuyPriceCopper: 100, SellPriceCopper: 200),
            new ItemCandidate("Missing Price"),
            new ItemCandidate("Existing Order", BuyPriceCopper: 100, SellPriceCopper: 200),
            new ItemCandidate("Good Item", Quantity: 2, BuyPriceCopper: 10_000, SellPriceCopper: 20_000)
        };
        var transactions = new TransactionIndex(new[]
        {
            new Transaction(TransactionType.Buy, "Existing Order", 100, 1, DateTimeOffset.UtcNow, null, null)
        });

        var decisions = new CandidatePlanner().Evaluate(
            candidates,
            transactions,
            new BlacklistMatcher(new[] { "Blacklisted *" }),
            new ProfitabilityPolicy(MinimumProfitPercentage: 50));

        Assert.Equal(4, decisions.Count);
        Assert.Equal(CandidateDecisionKind.Skipped, decisions[0].Kind);
        Assert.Contains("blacklisted", decisions[0].Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(CandidateDecisionKind.Skipped, decisions[1].Kind);
        Assert.Equal(CandidateDecisionKind.Skipped, decisions[2].Kind);
        Assert.Equal(CandidateDecisionKind.Accepted, decisions[3].Kind);
        Assert.Equal(2, decisions[3].Action!.Quantity);
    }

    [Fact]
    public async Task Controller_deduplicates_within_a_run_without_blocking_later_runs()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), "mmopal-trading-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var store = new CheckpointStore(databasePath);
            await store.InitializeAsync();
            var reader = new FlakyMarketReader(new MarketSnapshot(
                Array.Empty<Transaction>(),
                new AccountBalance(1_000_000),
                DateTimeOffset.UtcNow,
                "replay"));
            var executor = new RecordingExecutor();
            var controller = new TradingCycleController(
                store,
                NullLogger.Instance,
                reader,
                executor,
                new TradingControllerOptions
                {
                    MaxRetries = 1,
                    RetryBaseDelay = TimeSpan.Zero,
                    MaxMarketAge = TimeSpan.FromMinutes(1)
                });
            var candidates = new[]
            {
                new ItemCandidate("Profitable Item", Quantity: 2, BuyPriceCopper: 10_000, SellPriceCopper: 20_000)
            };

            var first = await controller.RunCycleAsync(
                "run-one",
                candidates,
                Array.Empty<string>(),
                new ProfitabilityPolicy(MinimumProfitPercentage: 50));
            var sameRunRepeat = await controller.RunCycleAsync(
                "run-one",
                candidates,
                Array.Empty<string>(),
                new ProfitabilityPolicy(MinimumProfitPercentage: 50));
            var nextRun = await controller.RunCycleAsync(
                "run-two",
                candidates,
                Array.Empty<string>(),
                new ProfitabilityPolicy(MinimumProfitPercentage: 50));

            Assert.True(first.Succeeded);
            Assert.Equal(4, reader.Attempts);
            Assert.Single(first.Actions);
            Assert.Equal(TradingActionStatus.Simulated, first.Actions[0].Status);
            Assert.Empty(sameRunRepeat.Actions);
            var repeatDecision = Assert.Single(sameRunRepeat.Decisions);
            Assert.Equal(CandidateDecisionKind.Skipped, repeatDecision.Kind);
            Assert.Contains("Order placed", repeatDecision.Reason, StringComparison.Ordinal);
            Assert.Single(nextRun.Actions);
            Assert.Equal(TradingActionStatus.Simulated, nextRun.Actions[0].Status);
            Assert.Equal(2, executor.Actions.Count);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task Controller_does_not_blindly_retry_a_failed_action_execution()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), "mmopal-trading-fail-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var store = new CheckpointStore(databasePath);
            await store.InitializeAsync();
            var reader = new ReplayMarketReader(new MarketSnapshot(
                Array.Empty<Transaction>(),
                new AccountBalance(1_000_000),
                DateTimeOffset.UtcNow,
                "replay"));
            var executor = new FailingExecutor();
            var controller = new TradingCycleController(
                store,
                NullLogger.Instance,
                reader,
                executor,
                new TradingControllerOptions
                {
                    MaxRetries = 3,
                    RetryBaseDelay = TimeSpan.Zero,
                    MaxMarketAge = TimeSpan.FromMinutes(1)
                });
            var candidates = new[]
            {
                new ItemCandidate("Profitable Item", Quantity: 1, BuyPriceCopper: 10_000, SellPriceCopper: 20_000)
            };

            var result = await controller.RunCycleAsync(
                "run-fail",
                candidates,
                Array.Empty<string>(),
                new ProfitabilityPolicy(MinimumProfitPercentage: 50));

            Assert.False(result.Succeeded);
            Assert.Equal(TradingState.Recovering, result.FinalState);
            Assert.Equal(1, executor.Attempts);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task Replay_market_reader_rebases_stale_timestamp_by_default()
    {
        var old = DateTimeOffset.UtcNow.AddHours(-2);
        var snapshot = new MarketSnapshot(Array.Empty<Transaction>(), new AccountBalance(10_000), old, "replay");

        var rebased = await new ReplayMarketReader(snapshot).ReadAsync();
        var preserved = await new ReplayMarketReader(snapshot, rebaseTimestamp: false).ReadAsync();

        Assert.True(rebased.ObservedAt > old);
        Assert.Equal(old, preserved.ObservedAt);
    }

    [Fact]
    public async Task Candidates_loader_reads_json_and_csv_candidates_and_blacklist()
    {
        var root = Path.Combine(Path.GetTempPath(), "mmopal-candidates-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var jsonPath = Path.Combine(root, "candidates.json");
            await File.WriteAllTextAsync(jsonPath, """[{"itemName":"Mystic Coin","quantity":2,"buyPriceCopper":10000,"sellPriceCopper":20000}]""");
            var csvPath = Path.Combine(root, "candidates.csv");
            await File.WriteAllTextAsync(csvPath, "ItemName;Quantity;BuyPriceCopper;SellPriceCopper\nGlob of Ectoplasm;5;2000;3000\n");
            var blacklistPath = Path.Combine(root, "blacklist.txt");
            await File.WriteAllTextAsync(blacklistPath, "ItemName\n# comment\nUnwanted *\n");

            var fromJson = await CandidatesLoader.LoadCandidatesAsync(jsonPath);
            var fromCsv = await CandidatesLoader.LoadCandidatesAsync(csvPath);
            var blacklist = await CandidatesLoader.LoadBlacklistAsync(blacklistPath);

            var jsonItem = Assert.Single(fromJson);
            Assert.Equal("Mystic Coin", jsonItem.ItemName);
            Assert.Equal(2, jsonItem.Quantity);
            Assert.Equal(10_000, jsonItem.BuyPriceCopper);
            Assert.Equal(20_000, jsonItem.SellPriceCopper);

            var csvItem = Assert.Single(fromCsv);
            Assert.Equal("Glob of Ectoplasm", csvItem.ItemName);
            Assert.Equal(5, csvItem.Quantity);
            Assert.Equal(2_000, csvItem.BuyPriceCopper);
            Assert.Equal(3_000, csvItem.SellPriceCopper);

            Assert.Equal(["Unwanted *"], blacklist);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Zero_gold_balance_pauses_all_buy_orders_even_without_floor()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"mmopal-zero-gold-{Guid.NewGuid():N}.db");
        try
        {
            var store = new CheckpointStore(dbPath);
            await store.InitializeAsync();

            var snapshot = new MarketSnapshot(Array.Empty<Transaction>(), new AccountBalance(0), DateTimeOffset.UtcNow, "replay");
            var executor = new RecordingExecutor();
            var controller = new TradingCycleController(
                store,
                NullLogger.Instance,
                new ReplayMarketReader(snapshot),
                executor,
                new TradingControllerOptions { MinimumBalanceCopper = 0, MaxActionsPerCycle = 5 });

            var result = await controller.RunCycleAsync(
                "run-zero-gold",
                [new ItemCandidate("Glob of Ectoplasm", Quantity: 1, BuyPriceCopper: 1_000, SellPriceCopper: 3_000)],
                Array.Empty<string>(),
                new ProfitabilityPolicy());

            Assert.True(result.Succeeded);
            Assert.Empty(executor.Actions);
            var decision = Assert.Single(result.Decisions);
            Assert.Equal(CandidateDecisionKind.Skipped, decision.Kind);
            Assert.Contains("0g", decision.Reason, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDatabase(dbPath);
        }
    }

    [Fact]
    public async Task Balance_at_or_below_reserve_floor_pauses_all_buy_orders()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"mmopal-floor-{Guid.NewGuid():N}.db");
        try
        {
            var store = new CheckpointStore(dbPath);
            await store.InitializeAsync();

            var snapshot = new MarketSnapshot(Array.Empty<Transaction>(), new AccountBalance(50_000), DateTimeOffset.UtcNow, "replay");
            var executor = new RecordingExecutor();
            var controller = new TradingCycleController(
                store,
                NullLogger.Instance,
                new ReplayMarketReader(snapshot),
                executor,
                new TradingControllerOptions { MinimumBalanceCopper = 50_000, MaxActionsPerCycle = 5 });

            var result = await controller.RunCycleAsync(
                "run-floor",
                [new ItemCandidate("Glob of Ectoplasm", Quantity: 1, BuyPriceCopper: 1_000, SellPriceCopper: 3_000)],
                Array.Empty<string>(),
                new ProfitabilityPolicy());

            Assert.True(result.Succeeded);
            Assert.Empty(executor.Actions);
            var decision = Assert.Single(result.Decisions);
            Assert.Equal(CandidateDecisionKind.Skipped, decision.Kind);
            Assert.Contains("reserve floor", decision.Reason, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDatabase(dbPath);
        }
    }

    [Fact]
    public async Task Running_spendable_budget_skips_later_buys_once_remaining_funds_exhausted()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"mmopal-budget-{Guid.NewGuid():N}.db");
        try
        {
            var store = new CheckpointStore(dbPath);
            await store.InitializeAsync();

            // 50,000c wallet with 10,000c reserve floor -> 40,000c spendable.
            // Two 25,000c orders total 50,000c, so only the first fits inside spendable budget.
            var snapshot = new MarketSnapshot(Array.Empty<Transaction>(), new AccountBalance(50_000), DateTimeOffset.UtcNow, "replay");
            var executor = new RecordingExecutor();
            var controller = new TradingCycleController(
                store,
                NullLogger.Instance,
                new ReplayMarketReader(snapshot),
                executor,
                new TradingControllerOptions { MinimumBalanceCopper = 10_000, MaxActionsPerCycle = 5 });

            var result = await controller.RunCycleAsync(
                "run-budget",
                [
                    new ItemCandidate("Item One", Quantity: 1, BuyPriceCopper: 25_000, SellPriceCopper: 40_000),
                    new ItemCandidate("Item Two", Quantity: 1, BuyPriceCopper: 25_000, SellPriceCopper: 40_000)
                ],
                Array.Empty<string>(),
                new ProfitabilityPolicy());

            Assert.True(result.Succeeded);
            var executed = Assert.Single(executor.Actions);
            Assert.Equal("Item One", executed.ItemName);
            Assert.Equal(CandidateDecisionKind.Skipped, result.Decisions[1].Kind);
            Assert.Contains("Insufficient spendable gold", result.Decisions[1].Reason, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDatabase(dbPath);
        }
    }

    [Fact]
    public async Task Mid_cycle_pause_stops_before_executing_remaining_actions()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"mmopal-pause-{Guid.NewGuid():N}.db");
        try
        {
            var store = new CheckpointStore(dbPath);
            await store.InitializeAsync();

            var snapshot = new MarketSnapshot(Array.Empty<Transaction>(), new AccountBalance(1_000_000), DateTimeOffset.UtcNow, "replay");
            var executor = new RecordingExecutor();
            var controller = new TradingCycleController(
                store,
                NullLogger.Instance,
                new ReplayMarketReader(snapshot),
                executor,
                new TradingControllerOptions { MaxActionsPerCycle = 5 });

            var paused = false;
            var result = await controller.RunCycleAsync(
                "run-mid-pause",
                [
                    new ItemCandidate("First Item", Quantity: 1, BuyPriceCopper: 1_000, SellPriceCopper: 3_000),
                    new ItemCandidate("Second Item", Quantity: 1, BuyPriceCopper: 1_000, SellPriceCopper: 3_000)
                ],
                Array.Empty<string>(),
                new ProfitabilityPolicy(),
                isPaused: () =>
                {
                    if (executor.Actions.Count >= 1) paused = true;
                    return paused;
                });

            Assert.True(result.Succeeded);
            var single = Assert.Single(executor.Actions);
            Assert.Equal("First Item", single.ItemName);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDatabase(dbPath);
        }
    }

    [Fact]
    public async Task Recently_ordered_items_do_not_starve_the_per_cycle_cap()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"mmopal-starve-{Guid.NewGuid():N}.db");
        try
        {
            var store = new CheckpointStore(dbPath);
            await store.InitializeAsync();

            var now = new DateTimeOffset(2026, 11, 20, 6, 0, 0, TimeSpan.Zero);
            var snapshot = new MarketSnapshot(Array.Empty<Transaction>(), new AccountBalance(1_000_000), now, "replay");
            var executor = new RecordingExecutor();
            var controller = new TradingCycleController(
                store,
                NullLogger.Instance,
                new ReplayMarketReader(snapshot),
                executor,
                new TradingControllerOptions { MaxActionsPerCycle = 2, UtcNow = () => now });
            ItemCandidate[] candidates =
            [
                new("Item A", Quantity: 1, BuyPriceCopper: 1_000, SellPriceCopper: 3_000),
                new("Item B", Quantity: 1, BuyPriceCopper: 1_000, SellPriceCopper: 3_000),
                new("Item C", Quantity: 1, BuyPriceCopper: 1_000, SellPriceCopper: 3_000),
                new("Item D", Quantity: 1, BuyPriceCopper: 1_000, SellPriceCopper: 3_000)
            ];

            await controller.RunCycleAsync("run-starve", candidates, Array.Empty<string>(), new ProfitabilityPolicy());
            // A and B were ordered but the API hasn't listed them yet: the cap must move on to C and D.
            now = now.AddMinutes(2);
            var second = await controller.RunCycleAsync("run-starve", candidates, Array.Empty<string>(), new ProfitabilityPolicy());
            // After the cooldown, an item that left current/buys (filled or canceled) can be re-ordered.
            now = now.AddMinutes(5);
            var third = await controller.RunCycleAsync("run-starve", candidates, Array.Empty<string>(), new ProfitabilityPolicy());

            Assert.Equal(["Item C", "Item D"], second.Actions.Select(a => a.Action.ItemName));
            Assert.All(second.Actions, a => Assert.Equal(TradingActionStatus.Simulated, a.Status));
            Assert.Equal(["Item A", "Item B"], third.Actions.Select(a => a.Action.ItemName));
            Assert.Equal(6, executor.Actions.Count);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDatabase(dbPath);
        }
    }

    [Fact]
    public async Task Cooldown_is_per_item_so_a_moved_price_does_not_double_order()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"mmopal-reprice-{Guid.NewGuid():N}.db");
        try
        {
            var store = new CheckpointStore(dbPath);
            await store.InitializeAsync();

            var now = new DateTimeOffset(2026, 11, 20, 6, 0, 0, TimeSpan.Zero);
            var snapshot = new MarketSnapshot(Array.Empty<Transaction>(), new AccountBalance(1_000_000), now, "replay");
            var executor = new RecordingExecutor();
            var controller = new TradingCycleController(
                store,
                NullLogger.Instance,
                new ReplayMarketReader(snapshot),
                executor,
                new TradingControllerOptions { UtcNow = () => now });

            await controller.RunCycleAsync(
                "run-reprice",
                [new ItemCandidate("Strong Ring", Quantity: 2, BuyPriceCopper: 3_872, SellPriceCopper: 8_000)],
                Array.Empty<string>(),
                new ProfitabilityPolicy());
            now = now.AddMinutes(1);
            var repriced = await controller.RunCycleAsync(
                "run-reprice",
                [new ItemCandidate("Strong Ring", Quantity: 2, BuyPriceCopper: 3_873, SellPriceCopper: 8_000)],
                Array.Empty<string>(),
                new ProfitabilityPolicy());

            Assert.Empty(repriced.Actions);
            Assert.Single(executor.Actions);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDatabase(dbPath);
        }
    }

    [Fact]
    public async Task Item_whose_search_lands_on_a_longer_named_item_is_not_bought_again()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"mmopal-mismatch-{Guid.NewGuid():N}.db");
        try
        {
            var store = new CheckpointStore(dbPath);
            await store.InitializeAsync();

            var now = new DateTimeOffset(2026, 11, 20, 6, 39, 0, TimeSpan.Zero);
            var wrongOrder = new Transaction(TransactionType.Buy, "Slice of Allspice Cake with Ice Cream", 4_955, 2, now, null, 4_972);
            var reader = new SwitchableMarketReader(new MarketSnapshot(Array.Empty<Transaction>(), new AccountBalance(1_000_000), now, "replay"));
            var executor = new CompletingExecutor();
            var controller = new TradingCycleController(
                store,
                NullLogger.Instance,
                reader,
                executor,
                new TradingControllerOptions { UtcNow = () => now });
            ItemCandidate[] candidates = [new("Slice of Allspice Cake", Quantity: 2, BuyPriceCopper: 5_404, SellPriceCopper: 10_562)];

            await controller.RunCycleAsync("run-cake", candidates, Array.Empty<string>(), new ProfitabilityPolicy());
            // Cooldown has passed, and the API shows the order that the search actually placed.
            now = now.AddMinutes(8);
            reader.Snapshot = new MarketSnapshot([wrongOrder], new AccountBalance(1_000_000), now, "replay");
            var second = await controller.RunCycleAsync("run-cake", candidates, Array.Empty<string>(), new ProfitabilityPolicy());

            Assert.Single(executor.Actions);
            Assert.Empty(second.Actions);
            var decision = Assert.Single(second.Decisions);
            Assert.Contains("with Ice Cream", decision.Reason, StringComparison.Ordinal);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDatabase(dbPath);
        }
    }

    private sealed class SwitchableMarketReader(MarketSnapshot snapshot) : IGw2MarketReader
    {
        public MarketSnapshot Snapshot { get; set; } = snapshot;

        public Task<MarketSnapshot> ReadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Snapshot with { ObservedAt = DateTimeOffset.UtcNow });
    }

    private sealed class CompletingExecutor : ITradingActionExecutor
    {
        public List<TradingAction> Actions { get; } = new();

        public Task<TradingActionResult> ExecuteAsync(TradingAction action, CancellationToken cancellationToken = default)
        {
            Actions.Add(action);
            return Task.FromResult(new TradingActionResult(action, TradingActionStatus.Completed, "placed"));
        }
    }

    private static void DeleteDatabase(string databasePath)
    {
        File.Delete(databasePath);
        File.Delete(databasePath + "-shm");
        File.Delete(databasePath + "-wal");
    }

    private sealed class FlakyMarketReader : IGw2MarketReader
    {
        private readonly MarketSnapshot _snapshot;

        public FlakyMarketReader(MarketSnapshot snapshot) => _snapshot = snapshot;

        public int Attempts { get; private set; }

        public Task<MarketSnapshot> ReadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Attempts++;
            if (Attempts == 1) throw new InvalidOperationException("simulated market read failure");
            return Task.FromResult(_snapshot);
        }
    }

    private sealed class RecordingExecutor : ITradingActionExecutor
    {
        public List<TradingAction> Actions { get; } = new();

        public Task<TradingActionResult> ExecuteAsync(TradingAction action, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Actions.Add(action);
            return Task.FromResult(new TradingActionResult(action, TradingActionStatus.Simulated, "recorded"));
        }
    }

    private sealed class FailingExecutor : ITradingActionExecutor
    {
        public int Attempts { get; private set; }

        public Task<TradingActionResult> ExecuteAsync(TradingAction action, CancellationToken cancellationToken = default)
        {
            Attempts++;
            throw new InvalidOperationException("simulated execution failure");
        }
    }
}
