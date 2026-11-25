using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using Mmopal.Contracts;
using Mmopal.Gw2;
using Mmopal.Infrastructure;

namespace Mmopal.Domain.Tests;

public sealed class TradingIncomeAnalyticsTests
{
    [Fact]
    public async Task Ledger_store_persists_deduplicates_transactions_snapshots_and_item_cache()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "mmopal-ledger-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var store = new TradingLedgerStore(Path.Combine(tempDir, "mmopal.db"));
            await store.InitializeAsync();

            var t0 = new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
            var batch1 = new[]
            {
                new Gw2LedgerTransaction(1001, TransactionType.Buy, 19976, "Mystic Coin", 10_000, 5, t0, t0.AddMinutes(5)),
                new Gw2LedgerTransaction(1002, TransactionType.Sell, 19976, "Mystic Coin", 20_000, 5, t0.AddHours(1), t0.AddHours(2))
            };
            Assert.Equal(2, await store.UpsertTransactionsAsync(batch1));

            var batch2 = new[]
            {
                batch1[0],
                new Gw2LedgerTransaction(1003, TransactionType.Buy, 19721, "Glob of Ectoplasm", 3_000, 10, t0.AddHours(3), t0.AddHours(3))
            };
            Assert.Equal(1, await store.UpsertTransactionsAsync(batch2));

            var loaded = await store.GetTransactionsAsync();
            Assert.Equal(3, loaded.Count);
            Assert.Equal(1001, loaded[0].Id);
            Assert.Equal(1003, loaded[2].Id);

            await store.SaveEquitySnapshotAsync(new Gw2EquitySnapshot(t0, 50_000, 1_000, 0, 10_000, 18_000, 79_000, 1, 1));
            await store.SaveEquitySnapshotAsync(new Gw2EquitySnapshot(t0.AddDays(2), 70_000, 0, 0, 0, 18_000, 88_000, 0, 1));
            var allSnaps = await store.GetEquitySnapshotsAsync();
            Assert.Equal(2, allSnaps.Count);
            var recentSnaps = await store.GetEquitySnapshotsAsync(t0.AddDays(1));
            Assert.Single(recentSnaps);
            Assert.Equal(88_000, recentSnaps[0].TotalEquityCopper);

            await store.UpsertItemsAsync(
            [
                new Gw2CachedItemInfo(19976, "Mystic Coin", "Rare", "CraftingMaterial", 66, t0),
                new Gw2CachedItemInfo(19721, "Glob of Ectoplasm", "Rare", "CraftingMaterial", 12, t0)
            ]);
            var subset = await store.GetCachedItemsAsync([19976, 99999]);
            Assert.Single(subset);
            Assert.Equal("Mystic Coin", subset[19976].Name);
            Assert.Equal("CraftingMaterial", subset[19976].ItemType);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Income_analytics_matches_fifo_across_pre_window_buys_and_separates_unmatched_loot()
    {
        var vnTz = TimeZoneInfo.CreateCustomTimeZone("VN", TimeSpan.FromHours(7), "Vietnam", "Vietnam");
        var windowStart = new DateTimeOffset(2026, 11, 15, 0, 0, 0, TimeSpan.Zero);

        var rows = new[]
        {
            // Pre-window buy: 10 @ 1g
            new Gw2LedgerTransaction(1, TransactionType.Buy, 101, "Mystic Coin", 10_000, 10,
                new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 11, 10, 10, 0, 0, TimeSpan.Zero)),
            // In-window buy: 5 @ 1g 20s at 18:00 UTC (01:00 local next day)
            new Gw2LedgerTransaction(2, TransactionType.Buy, 101, "Mystic Coin", 12_000, 5,
                new DateTimeOffset(2026, 11, 16, 17, 30, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 11, 16, 18, 0, 0, TimeSpan.Zero)),
            // In-window sell: 12 @ 2g at 00:30 UTC (07:30 local) -> net unit 17,000 -> rev 204,000, FIFO cost 10*10,000 + 2*12,000 = 124,000 -> profit 80,000
            new Gw2LedgerTransaction(3, TransactionType.Sell, 101, "Mystic Coin", 20_000, 12,
                new DateTimeOffset(2026, 11, 16, 19, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 11, 17, 0, 30, 0, TimeSpan.Zero)),
            // Unmatched loot sale: 3 @ 50s -> net unit 4,250 -> 12,750
            new Gw2LedgerTransaction(4, TransactionType.Sell, 202, "Farmed Loot", 5_000, 3,
                new DateTimeOffset(2026, 11, 17, 0, 25, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 11, 17, 0, 30, 0, TimeSpan.Zero))
        };

        Assert.Equal(1, TradingIncomeAnalytics.CalculateListingFee(4));
        Assert.Equal(1, TradingIncomeAnalytics.CalculateExchangeFee(4));

        var report = TradingIncomeAnalytics.Analyze(rows, windowStart, vnTz);
        Assert.Equal(80_000, report.MatchedProfitCopper);
        Assert.Equal(124_000, report.MatchedCostBasisCopper);
        Assert.Equal(204_000, report.MatchedNetRevenueCopper);
        Assert.Equal(12_750, report.UnmatchedSellRevenueCopper);
        Assert.Equal(60_000, report.TotalBuysSpentCopper);
        Assert.Equal(12, report.MatchedUnitsSold);
        Assert.Equal(3, report.UnmatchedUnitsSold);
        Assert.Equal(100.0, report.WinRatePercent);

        Assert.Equal(24, report.HourlySeries.Count);
        Assert.Equal(1, report.HourlySeries[1].BuyFills);
        Assert.Equal(2, report.HourlySeries[7].SellFills);
        Assert.Equal(80_000, report.HourlySeries[7].RealizedProfitCopper);

        var itemRow = Assert.Single(report.ItemLeaderboard);
        Assert.Equal("Mystic Coin", itemRow.ItemName);
        Assert.Equal(12, itemRow.UnitsFlipped);
        Assert.Equal(80_000, itemRow.NetProfitCopper);
        Assert.Equal("Winner", itemRow.OutcomeBadge);
    }

    [Fact]
    public async Task Official_provider_syncs_portfolio_with_ninety_percent_active_sells_and_book_status()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "mmopal-sync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var store = new TradingLedgerStore(Path.Combine(tempDir, "mmopal.db"));
            using var client = new HttpClient(new StubHandler(request => request.RequestUri!.PathAndQuery switch
            {
                "/v2/commerce/transactions/history/buys?page=0&page_size=200" => Json("""
                    [{"id":10,"item_id":101,"price":10000,"quantity":4,"created":"2026-11-18T10:00:00Z","purchased":"2026-11-18T10:15:00Z"}]
                    """),
                "/v2/commerce/transactions/history/sells?page=0&page_size=200" => Json("""
                    [{"id":11,"item_id":101,"price":20000,"quantity":2,"created":"2026-11-18T11:00:00Z","purchased":"2026-11-18T12:00:00Z"}]
                    """),
                "/v2/commerce/transactions/current/buys?page=0&page_size=200" => Json("""
                    [
                      {"id":21,"item_id":101,"price":10000,"quantity":2,"created":"2026-11-19T01:00:00Z"},
                      {"id":22,"item_id":101,"price":9000,"quantity":1,"created":"2026-11-19T01:05:00Z"}
                    ]
                    """),
                "/v2/commerce/transactions/current/sells?page=0&page_size=200" => Json("""
                    [
                      {"id":31,"item_id":101,"price":20000,"quantity":2,"created":"2026-11-19T02:00:00Z"},
                      {"id":32,"item_id":101,"price":22000,"quantity":1,"created":"2026-11-19T02:05:00Z"}
                    ]
                    """),
                "/v2/commerce/delivery" => Json("""{"coins":5000,"items":[{"id":101,"count":3}]}"""),
                "/v2/account/wallet" => Json("""[{"id":1,"value":100000}]"""),
                "/v2/commerce/prices?ids=101" => Json("""[{"id":101,"buys":{"quantity":100,"unit_price":10000},"sells":{"quantity":100,"unit_price":20000}}]"""),
                "/v2/items?ids=101" => Json("""[{"id":101,"name":"Mystic Coin","rarity":"Rare","type":"CraftingMaterial","vendor_value":66}]"""),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            }))
            {
                BaseAddress = new Uri("https://api.guildwars2.com/")
            };

            var provider = new Gw2OfficialDataProvider(client, "test-api-key");
            var result = await provider.SyncPortfolioAsync(store);

            Assert.Equal(2, result.NewlyArchivedTransactions);
            Assert.Equal(2, result.TotalArchivedTransactions);
            Assert.Equal(100_000, result.LatestSnapshot.WalletCopper);
            Assert.Equal(5_000, result.LatestSnapshot.DeliveryCopper);
            Assert.Equal(30_000, result.LatestSnapshot.DeliveryItemsCopper);
            Assert.Equal(29_000, result.LatestSnapshot.ActiveBuysCopper);
            // Active sells use 90% net (only 10% exchange fee deducted; 5% listing already paid upfront).
            Assert.Equal(55_800, result.LatestSnapshot.ActiveSellsNetCopper);
            Assert.Equal(219_800, result.LatestSnapshot.TotalEquityCopper);

            Assert.Equal(4, result.ActiveOrders.Count);
            Assert.Equal("Top of Book", result.ActiveOrders.Single(o => o.Id == 21).BookStatus);
            Assert.Equal("Outbid", result.ActiveOrders.Single(o => o.Id == 22).BookStatus);
            Assert.Equal("Top of Book", result.ActiveOrders.Single(o => o.Id == 31).BookStatus);
            Assert.Equal("Undercut", result.ActiveOrders.Single(o => o.Id == 32).BookStatus);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Market_cycle_and_strategy_presets_cover_all_phases_and_bankroll_tiers()
    {
        var vnTz = TimeZoneInfo.CreateCustomTimeZone("VN", TimeSpan.FromHours(7), "Vietnam", "Vietnam");
        Assert.Equal("Daily Reset Spike", Gw2MarketCycle.Describe(new DateTimeOffset(2026, 11, 19, 0, 30, 0, TimeSpan.Zero), vnTz).PhaseName);
        Assert.Equal("EU Prime Time", Gw2MarketCycle.Describe(new DateTimeOffset(2026, 11, 19, 19, 0, 0, TimeSpan.Zero), vnTz).PhaseName);
        Assert.Equal("NA Prime Time", Gw2MarketCycle.Describe(new DateTimeOffset(2026, 11, 19, 23, 30, 0, TimeSpan.Zero), vnTz).PhaseName);
        Assert.Equal("NA Prime Time", Gw2MarketCycle.Describe(new DateTimeOffset(2026, 11, 19, 2, 30, 0, TimeSpan.Zero), vnTz).PhaseName);
        Assert.Equal("Global Off-Peak", Gw2MarketCycle.Describe(new DateTimeOffset(2026, 11, 19, 10, 0, 0, TimeSpan.Zero), vnTz).PhaseName);

        var starter = TradingStrategyPresets.RecommendForCapital(50 * 10_000L);
        Assert.StartsWith("Starter", starter.Name, StringComparison.Ordinal);
        Assert.InRange(starter.MaxBuyGold, 1m, 5m);
        Assert.Equal(10, starter.MaxSessionHours);

        var balanced = TradingStrategyPresets.RecommendForCapital(500 * 10_000L);
        Assert.StartsWith("Balanced", balanced.Name, StringComparison.Ordinal);
        Assert.InRange(balanced.MaxBuyGold, 5m, 35m);
        Assert.Equal(40m, balanced.RecommendedGoldFloor); // 8%

        // 25 Nov: 1,158g. High Capital would have dropped the items half of the profit came from.
        var grown = TradingStrategyPresets.RecommendForCapital(1_158 * 10_000L);
        Assert.StartsWith("Balanced", grown.Name, StringComparison.Ordinal);
        Assert.Equal(46m, grown.MaxBuyGold);
        Assert.Equal(93m, grown.RecommendedGoldFloor);
        Assert.NotNull(TradingStrategyPresets.FindByName(grown.Name));

        var high = TradingStrategyPresets.RecommendForCapital(3_500 * 10_000L);
        Assert.StartsWith("High Capital", high.Name, StringComparison.Ordinal);
        Assert.InRange(high.MaxBuyGold, 35m, 150m);
    }

    [Fact]
    public async Task Ledger_store_isolates_accounts_and_adopts_unassigned_legacy_rows()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "mmopal-ledger-acct-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var store = new TradingLedgerStore(Path.Combine(tempDir, "mmopal.db"));
            await store.InitializeAsync();

            var t0 = DateTimeOffset.UtcNow.AddDays(-2);
            await store.UpsertTransactionsAsync(
            [
                new Gw2LedgerTransaction(101, TransactionType.Buy, 19721, "Glob of Ectoplasm", 2_000, 1, t0, t0)
            ]);
            await store.SaveEquitySnapshotAsync(new Gw2EquitySnapshot(t0, 100_000, 0, 0, 0, 0, 100_000, 0, 0));

            await store.AdoptUnassignedRowsAsync("SanguinE.9435");

            await store.UpsertTransactionsAsync(
            [
                new Gw2LedgerTransaction(201, TransactionType.Buy, 19976, "Mystic Coin", 12_000, 1, t0.AddHours(1), t0.AddHours(1))
            ], "Hu Tao.1937");
            await store.SaveEquitySnapshotAsync(new Gw2EquitySnapshot(t0.AddHours(1), 500_000, 0, 0, 0, 0, 500_000, 0, 0), "Hu Tao.1937");

            var sanguinTx = await store.GetTransactionsAsync("SanguinE.9435");
            var hutaoTx = await store.GetTransactionsAsync("Hu Tao.1937");
            var sanguinSnaps = await store.GetEquitySnapshotsAsync(null, "SanguinE.9435");
            var hutaoSnaps = await store.GetEquitySnapshotsAsync(null, "Hu Tao.1937");

            Assert.Single(sanguinTx);
            Assert.Equal("Glob of Ectoplasm", sanguinTx[0].ItemName);
            Assert.Single(hutaoTx);
            Assert.Equal("Mystic Coin", hutaoTx[0].ItemName);
            Assert.Single(sanguinSnaps);
            Assert.Equal(100_000, sanguinSnaps[0].WalletCopper);
            Assert.Single(hutaoSnaps);
            Assert.Equal(500_000, hutaoSnaps[0].WalletCopper);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }
}
