using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using Mmopal.Gw2;
using Mmopal.Infrastructure;

namespace Mmopal.Domain.Tests;

public sealed class MarketScannerTests
{
    [Fact]
    public async Task Market_scanner_filters_prices_with_gw2_fees_supply_demand_rarity_and_blacklist()
    {
        var requestedPaths = new List<string>();
        using var client = new HttpClient(new StubHandler(request =>
        {
            var path = request.RequestUri!.PathAndQuery;
            requestedPaths.Add(path);
            return path switch
            {
                "/v2/commerce/prices" => Json("[101,102,103,104,105]"),
                "/v2/commerce/prices?ids=101,102,103,104,105" => Json("""
                    [
                      {"id":101,"buys":{"quantity":200,"unit_price":10000},"sells":{"quantity":150,"unit_price":15000}},
                      {"id":102,"buys":{"quantity":10,"unit_price":10000},"sells":{"quantity":150,"unit_price":15000}},
                      {"id":103,"buys":{"quantity":200,"unit_price":10000},"sells":{"quantity":150,"unit_price":11000}},
                      {"id":104,"buys":{"quantity":200,"unit_price":10000},"sells":{"quantity":150,"unit_price":16000}},
                      {"id":105,"buys":{"quantity":200,"unit_price":10000},"sells":{"quantity":150,"unit_price":17000}}
                    ]
                    """),
                _ when path.StartsWith("/v2/items?ids=", StringComparison.Ordinal) => Json("""
                    [
                      {"id":101,"name":"Glob of Ectoplasm","rarity":"Rare"},
                      {"id":104,"name":"Blacklisted Relic","rarity":"Rare"},
                      {"id":105,"name":"Fine Dust","rarity":"Fine"}
                    ]
                    """),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        }))
        {
            BaseAddress = new Uri("https://api.guildwars2.com/")
        };

        var scanner = new Gw2MarketScanner(client);
        var filter = new MarketScanFilter(
            MinimumProfitPercentage: 10m,
            MinimumProfitCopper: 500,
            MinimumBuyPriceCopper: 1_000,
            MaximumBuyPriceCopper: 100_000,
            MinimumSupply: 50,
            MinimumDemand: 50,
            Rarity: "Rare",
            MaxItemsToScan: 500,
            MaxResults: 20);

        var results = await scanner.ScanAsync(filter, blacklist: ["Blacklisted Relic"]);

        // 101: buy 10000, sell 15000 -> fees 750+1500=2250 -> net 12750 -> profit 2750 (27.5%), Rare -> KEEP
        // 102: demand 10 < 50 -> SKIP
        // 103: buy 10000, sell 11000 -> net 9350 -> unprofitable -> SKIP
        // 104: blacklisted -> SKIP
        // 105: Fine != Rare -> SKIP
        var match = Assert.Single(results);
        Assert.Equal(101, match.ItemId);
        Assert.Equal("Glob of Ectoplasm", match.ItemName);
        Assert.Equal("Rare", match.Rarity);
        Assert.Equal(2_750, match.NetProfitCopper);
        Assert.Equal(27.5m, match.ProfitPercentage);
        Assert.Equal("1g 00s 00c", match.BuyFormatted);
        Assert.Equal("1g 50s 00c", match.SellFormatted);
        Assert.Equal("27s 50c", match.ProfitFormatted);

        // Verify v2/items only queried profitable passing IDs (101, 104, 105), never 102 or 103.
        var itemsCall = Assert.Single(requestedPaths, p => p.StartsWith("/v2/items?ids=", StringComparison.Ordinal));
        Assert.DoesNotContain("102", itemsCall);
        Assert.DoesNotContain("103", itemsCall);
    }

    [Fact]
    public async Task Market_scanner_imports_gw2bltc_formatted_csv()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "mmopal-bltc-" + Guid.NewGuid().ToString("N") + ".csv");
        try
        {
            await File.WriteAllTextAsync(tempFile,
                "ItemName;Rarity;Sell;Buy;ROI;Quantity\n" +
                "Mystic Coin;Rare;1g 50s 00c;1g 00s 00c;27.5%;5\n");

            var items = await Gw2MarketScanner.ImportFromFileAsync(tempFile);
            var coin = Assert.Single(items);
            Assert.Equal("Mystic Coin", coin.ItemName);
            Assert.Equal("Rare", coin.Rarity);
            Assert.Equal(10_000, coin.BuyPriceCopper);
            Assert.Equal(15_000, coin.SellPriceCopper);
            Assert.Equal(2_750, coin.NetProfitCopper);
            Assert.Equal(5, coin.SuggestedQuantity);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task Official_provider_validates_api_key_scopes_and_wallet()
    {
        using var client = new HttpClient(new StubHandler(request => request.RequestUri!.PathAndQuery switch
        {
            "/v2/tokeninfo" => Json("""{"name":"Flipper","permissions":["account","tradingpost","wallet"]}"""),
            "/v2/account" => Json("""{"name":"Commander.1234"}"""),
            "/v2/account/wallet" => Json("""[{"id":1,"value":1254012}]"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        }))
        {
            BaseAddress = new Uri("https://api.guildwars2.com/")
        };

        var provider = new Gw2OfficialDataProvider(client, "test-api-key");
        var validation = await provider.ValidateApiKeyAsync();

        Assert.True(validation.IsValid);
        Assert.Equal("Commander.1234", validation.AccountName);
        Assert.Equal(1_254_012, validation.CoinCopper);
        Assert.Contains("tradingpost", validation.Permissions);
        Assert.Contains("verified", validation.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Market_scanner_supports_wall_depth_category_max_roi_cache_and_refilter()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "mmopal-scancache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var requestedPaths = new List<string>();
            using var client = new HttpClient(new StubHandler(request =>
            {
                var path = request.RequestUri!.PathAndQuery;
                requestedPaths.Add(path);
                return path switch
                {
                    "/v2/commerce/prices" => Json("[301,302,303]"),
                    "/v2/commerce/prices?ids=301,302,303" => Json("""
                        [
                          {"id":301,"buys":{"quantity":300,"unit_price":10000},"sells":{"quantity":400,"unit_price":14000}},
                          {"id":302,"buys":{"quantity":100,"unit_price":10000},"sells":{"quantity":900,"unit_price":15000}},
                          {"id":303,"buys":{"quantity":100,"unit_price":10000},"sells":{"quantity":100,"unit_price":30000}}
                        ]
                        """),
                    _ when path.StartsWith("/v2/items?ids=", StringComparison.Ordinal) => Json("""
                        [
                          {"id":301,"name":"Fine Material","rarity":"Fine","type":"CraftingMaterial","vendor_value":50},
                          {"id":302,"name":"Blocked Wall Sword","rarity":"Rare","type":"Weapon","vendor_value":100}
                        ]
                        """),
                    _ when path.StartsWith("/v2/commerce/listings?ids=", StringComparison.Ordinal) => Json("""
                        [
                          {"id":301,"buys":[{"listings":12,"unit_price":10000,"quantity":300},{"listings":8,"unit_price":9900,"quantity":200},{"listings":4,"unit_price":9800,"quantity":100}],"sells":[{"listings":8,"unit_price":14000,"quantity":100},{"listings":6,"unit_price":14200,"quantity":150},{"listings":4,"unit_price":14400,"quantity":150}]},
                          {"id":302,"buys":[{"listings":5,"unit_price":10000,"quantity":100},{"listings":2,"unit_price":9800,"quantity":50},{"listings":2,"unit_price":9700,"quantity":40}],"sells":[{"listings":1,"unit_price":15000,"quantity":900},{"listings":2,"unit_price":15100,"quantity":50},{"listings":2,"unit_price":15200,"quantity":40}]}
                        ]
                        """),
                    _ => new HttpResponseMessage(HttpStatusCode.NotFound)
                };
            }))
            {
                BaseAddress = new Uri("https://api.guildwars2.com/")
            };

            var cache = new TradingLedgerStore(Path.Combine(tempDir, "mmopal.db"));
            var scanner = new Gw2MarketScanner(client, cache);

            // First scan: Max ROI 100% skips 303 (155% ROI); CheckWallDepth catches 302's Sell Wall; overbid 10c/undercut 10c adjusts net profit.
            var firstFilter = new MarketScanFilter(
                MinimumProfitPercentage: 10m,
                MinimumProfitCopper: 100,
                MinimumBuyPriceCopper: 1_000,
                MaximumBuyPriceCopper: 100_000,
                MinimumSupply: 50,
                MinimumDemand: 50,
                MaxItemsToScan: 500,
                MaxResults: 20,
                MaximumProfitPercentage: 100m,
                OverbidCopper: 10,
                UndercutCopper: 10,
                CheckWallDepth: true);

            var firstResults = await scanner.ScanAsync(firstFilter);
            Assert.Equal(2, firstResults.Count);
            Assert.Equal("Liquid", firstResults.Single(r => r.ItemId == 301).RiskBadge);
            Assert.Equal("Sell Wall", firstResults.Single(r => r.ItemId == 302).RiskBadge);
            Assert.Equal(10_010, firstResults.Single(r => r.ItemId == 301).BuyPriceCopper);
            Assert.Equal(13_990, firstResults.Single(r => r.ItemId == 301).SellPriceCopper);
            Assert.Contains("5% list", firstResults.Single(r => r.ItemId == 301).FeeBreakdownTooltip, StringComparison.Ordinal);

            var callsAfterFirst = requestedPaths.Count;
            Assert.Equal(1, requestedPaths.Count(p => p.StartsWith("/v2/items?ids=", StringComparison.Ordinal)));

            // Re-filter by CraftingMaterial using cached prices + SQLite item cache -> zero v2/commerce/prices and zero v2/items calls!
            var secondFilter = firstFilter with { ItemType = "CraftingMaterial", CheckWallDepth = false };
            var secondResults = await scanner.ReFilterCachedAsync(secondFilter);

            var mat = Assert.Single(secondResults);
            Assert.Equal(301, mat.ItemId);
            Assert.Equal("CraftingMaterial", mat.ItemType);
            Assert.Equal(callsAfterFirst, requestedPaths.Count);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Gold_per_day_ranking_drops_dead_wide_spreads_and_prefers_items_that_trade()
    {
        using var client = PricesClient();
        using var velocity = new HttpClient(new StubHandler(_ => Json("""
            [
              {"id":201,"7d_buy_sold":2,"7d_sell_sold":3},
              {"id":202,"7d_buy_sold":3500,"7d_sell_sold":4000},
              {"id":203,"7d_buy_sold":210,"7d_sell_sold":400}
            ]
            """)));
        var scanner = new Gw2MarketScanner(client, velocityHttpClient: velocity);

        var results = await scanner.ScanAsync(VolumeFilter(MarketScanSortModes.GoldPerDay, minimumDailyTrades: 5));

        // 201: 127% ROI but ~0.3 trades/day -> dropped.
        // 202: 1,050c profit x min(500/day, 20) = 21,000; 203: 950c x min(30/day, 20) = 19,000.
        Assert.Equal(["Busy Item", "Steady Item"], results.Select(item => item.ItemName));
        Assert.Equal(500, results[0].DailyVolumeEstimate);
        Assert.Equal(30, results[1].DailyVolumeEstimate);
    }

    [Fact]
    public async Task Gold_per_day_scan_still_works_when_trade_volume_is_unavailable()
    {
        using var client = PricesClient();
        using var velocity = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        var scanner = new Gw2MarketScanner(client, velocityHttpClient: velocity);

        var results = await scanner.ScanAsync(VolumeFilter(MarketScanSortModes.GoldPerDay, minimumDailyTrades: 5));

        // No volume data: nothing is dropped for volume and ranking falls back to spread/depth.
        Assert.Equal(3, results.Count);
        Assert.All(results, item => Assert.Null(item.DailyVolumeEstimate));
    }

    private static HttpClient PricesClient() => new(new StubHandler(request =>
    {
        var path = request.RequestUri!.PathAndQuery;
        return path switch
        {
            "/v2/commerce/prices" => Json("[201,202,203]"),
            _ when path.StartsWith("/v2/commerce/prices?ids=", StringComparison.Ordinal) => Json("""
                [
                  {"id":201,"buys":{"quantity":40,"unit_price":3000},"sells":{"quantity":12,"unit_price":8000}},
                  {"id":202,"buys":{"quantity":900,"unit_price":10000},"sells":{"quantity":700,"unit_price":13000}},
                  {"id":203,"buys":{"quantity":300,"unit_price":5000},"sells":{"quantity":250,"unit_price":7000}}
                ]
                """),
            _ when path.StartsWith("/v2/items?ids=", StringComparison.Ordinal) => Json("""
                [
                  {"id":201,"name":"Dead Spread Item","rarity":"Rare"},
                  {"id":202,"name":"Busy Item","rarity":"Rare"},
                  {"id":203,"name":"Steady Item","rarity":"Rare"}
                ]
                """),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        };
    }))
    {
        BaseAddress = new Uri("https://api.guildwars2.com/")
    };

    private static MarketScanFilter VolumeFilter(string sortMode, double minimumDailyTrades) => new(
        MinimumProfitPercentage: 5m,
        MinimumProfitCopper: 100,
        MinimumBuyPriceCopper: 1_000,
        MaximumBuyPriceCopper: 100_000,
        MinimumSupply: 1,
        MinimumDemand: 1,
        MaxItemsToScan: 500,
        MaxResults: 20,
        SortMode: sortMode,
        MinimumDailyTrades: minimumDailyTrades);

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_handler(request));
    }
}
