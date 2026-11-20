using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mmopal.Contracts;
using Mmopal.Infrastructure;

namespace Mmopal.Gw2;

public sealed record MarketScanFilter(
    decimal MinimumProfitPercentage = 10m,
    long MinimumProfitCopper = 500,
    long MinimumBuyPriceCopper = 1_000,
    long? MaximumBuyPriceCopper = 500_000,
    long MinimumSupply = 50,
    long MinimumDemand = 50,
    string? Rarity = null,
    int MaxItemsToScan = 2_000,
    int MaxResults = 100,
    long DefaultQuantity = 1,
    decimal? MaximumProfitPercentage = null,
    string? ItemType = null,
    int OverbidCopper = 0,
    int UndercutCopper = 0,
    bool CheckWallDepth = false,
    bool IncludeDataWarsVelocity = false,
    string SortMode = "BalancedScore",
    // Units that actually trade per day (DataWars2 7-day average of min(filled buy orders, bought listings)).
    // A flip needs both sides to move; wide-spread items that trade a few times a day just sit unfilled.
    double MinimumDailyTrades = 0,
    // Skip items whose top bid or lowest ask is more than SpikeTolerance above its 7-day average (DataWars2):
    // the projected profit of a spiked price is not real, and buying into a spike is how flips lose money.
    bool SkipPriceSpikes = true,
    double SpikeTolerance = 0.30);

/// <summary>DataWars2 per-item stats: trades/day (slower side, 7-day average) and 7-day average prices.</summary>
public sealed record MarketItemStats(double TradesPerDay, long? BuyAvg7dCopper, long? SellAvg7dCopper);

public static class MarketScanSortModes
{
    /// <summary>Net profit per unit × trades/day (capped): the "gold per day" ranking experienced flippers use.</summary>
    public const string GoldPerDay = "GoldPerDay";

    /// <summary>Beyond this many trades/day extra volume no longer helps a bot placing a few small orders.</summary>
    public const double TradesPerDayCap = 20;
}

public sealed record MarketScanProgress(
    int ScannedItems,
    int TotalItems,
    int MatchesFound,
    string Phase);

public sealed record MarketScanItem(
    int ItemId,
    string ItemName,
    string Rarity,
    long BuyPriceCopper,
    long SellPriceCopper,
    long NetProfitCopper,
    decimal ProfitPercentage,
    long BuyQuantity,
    long SellQuantity,
    long SuggestedQuantity = 1,
    string ItemType = "",
    long VendorValueCopper = 0,
    string RiskBadge = "Liquid",
    string FeeBreakdownTooltip = "",
    int? DailyVolumeEstimate = null,
    double LiquidityScore = 0,
    // What your own trade history says about this item (Income & Portfolio): boost, slow, or skipped as a loser.
    string HistoryNote = "")
{
    public string BuyFormatted => FormatCoins(BuyPriceCopper);
    public string SellFormatted => FormatCoins(SellPriceCopper);
    public string ProfitFormatted => FormatCoins(NetProfitCopper);
    public string RoiFormatted => $"{ProfitPercentage:0.0}%";
    public string VolumeFormatted => DailyVolumeEstimate.HasValue ? $"{DailyVolumeEstimate.Value:N0}/d" : "—";

    public ItemCandidate ToCandidate() => new(
        ItemName: ItemName,
        Rarity: Rarity,
        Quantity: Math.Max(1, SuggestedQuantity),
        Roi: decimal.Round(ProfitPercentage, 2),
        SellPriceCopper: SellPriceCopper,
        BuyPriceCopper: BuyPriceCopper,
        ItemId: ItemId);

    public static string FormatCoins(long totalCopper)
    {
        var negative = totalCopper < 0;
        var abs = Math.Abs(totalCopper);
        var (gold, silver, copper) = CurrencyMath.FromCopper(abs);
        var text = gold > 0
            ? $"{gold}g {silver:00}s {copper:00}c"
            : silver > 0
                ? $"{silver}s {copper:00}c"
                : $"{copper}c";
        return negative ? "-" + text : text;
    }
}

public interface IGw2MarketScanner
{
    Task<IReadOnlyList<MarketScanItem>> ScanAsync(
        MarketScanFilter filter,
        IReadOnlyCollection<string>? blacklist = null,
        IProgress<MarketScanProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Queries the official Guild Wars 2 v2/commerce/prices, v2/commerce/listings, and v2/items endpoints
/// (with optional local SQLite item caching and best-effort DataWars2 volume enrichment)
/// to discover liquid, profitable Trading Post flips.
/// </summary>
public sealed class Gw2MarketScanner : IGw2MarketScanner
{
    private const int ApiBatchSize = 200;
    private const int MaxConcurrentRequests = 4;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly TradingLedgerStore? _itemCache;
    private readonly HttpClient? _velocityHttpClient;
    private readonly ProfitabilityEngine _profitability = new();
    private readonly SemaphoreSlim _cacheInitGate = new(1, 1);
    private bool _cacheInitialized;
    private IReadOnlyList<Gw2CommercePrice>? _cachedPrices;
    private Dictionary<int, MarketItemStats>? _cachedStats;
    // Min trades/day the cached prices were pre-selected with (volume-first scan); 0 = not pre-selected.
    private double _cachedSelectionMinTrades;
    // DataWars2 was asked for during the cached scan (it may have been unreachable; do not re-scan just to retry).
    private bool _cachedStatsAttempted;
    private DateTimeOffset _statsFetchedAtUtc = DateTimeOffset.MinValue;
    private static readonly TimeSpan StatsReuseWindow = TimeSpan.FromMinutes(30);

    public Gw2MarketScanner(
        HttpClient httpClient,
        TradingLedgerStore? itemCache = null,
        HttpClient? velocityHttpClient = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _itemCache = itemCache;
        _velocityHttpClient = velocityHttpClient;
    }

    public bool HasCachedScan => _cachedPrices is { Count: > 0 };

    /// <summary>Items the last scan or re-filter skipped as price spikes (vs the 7-day average).</summary>
    public int LastSpikesSkipped { get; private set; }

    public async Task<IReadOnlyList<MarketScanItem>> ScanAsync(
        MarketScanFilter filter,
        IReadOnlyCollection<string>? blacklist = null,
        IProgress<MarketScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        progress?.Report(new MarketScanProgress(0, 0, 0, "Fetching tradable item index from GW2 API..."));
        var allIds = await GetJsonAsync<int[]>("v2/commerce/prices", cancellationToken).ConfigureAwait(false);
        if (allIds.Length == 0)
        {
            return Array.Empty<MarketScanItem>();
        }

        _cachedStatsAttempted = NeedsStats(filter);
        if (!_cachedStatsAttempted)
        {
            _cachedStats = null;
        }
        else if (_cachedStats is null || DateTimeOffset.UtcNow - _statsFetchedAtUtc >= StatsReuseWindow)
        {
            // 7-day averages barely move within half an hour, and the download takes ~20 s, so re-scans reuse it.
            progress?.Report(new MarketScanProgress(0, allIds.Length, 0, "Fetching 7-day trade volume and prices (DataWars2, ~20 s)..."));
            _cachedStats = await FetchDataWarsStatsBestEffortAsync(cancellationToken).ConfigureAwait(false);
            _statsFetchedAtUtc = DateTimeOffset.UtcNow;
        }

        var selectedIds = SelectIdsToPrice(allIds, filter, _cachedStats);
        _cachedSelectionMinTrades = _cachedStats is not null && filter.MinimumDailyTrades > 0 ? filter.MinimumDailyTrades : 0;
        if (_cachedSelectionMinTrades > 0)
        {
            progress?.Report(new MarketScanProgress(0, selectedIds.Length, 0,
                $"Whole market: {selectedIds.Length:N0} of {allIds.Length:N0} items trade {filter.MinimumDailyTrades:0.#}+/day; pricing those..."));
        }

        var rawPrices = new ConcurrentBag<Gw2CommercePrice>();
        var chunks = selectedIds.Chunk(ApiBatchSize).ToArray();
        var scannedCount = 0;

        using var throttle = new SemaphoreSlim(MaxConcurrentRequests, MaxConcurrentRequests);
        var tasks = chunks.Select(async chunk =>
        {
            await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var idsParam = string.Join(',', chunk);
                var prices = await GetJsonAsync<List<Gw2CommercePrice>>($"v2/commerce/prices?ids={idsParam}", cancellationToken).ConfigureAwait(false);
                foreach (var entry in prices)
                {
                    rawPrices.Add(entry);
                }

                var done = Interlocked.Add(ref scannedCount, chunk.Length);
                progress?.Report(new MarketScanProgress(done, selectedIds.Length, rawPrices.Count, "Downloading GW2 Trading Post prices..."));
            }
            finally
            {
                throttle.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
        _cachedPrices = rawPrices.ToArray();

        return await EvaluatePricesAsync(_cachedPrices, filter, blacklist, progress, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<MarketScanItem>> ReFilterCachedAsync(
        MarketScanFilter filter,
        IReadOnlyCollection<string>? blacklist = null,
        IProgress<MarketScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        // A volume-first scan priced only items trading at least N/day; a lower threshold needs a fresh scan.
        if (_cachedPrices is not { Count: > 0 } cached ||
            (_cachedSelectionMinTrades > 0 && filter.MinimumDailyTrades < _cachedSelectionMinTrades) ||
            (!_cachedStatsAttempted && NeedsStats(filter)))
        {
            return ScanAsync(filter, blacklist, progress, cancellationToken);
        }

        return EvaluatePricesAsync(cached, filter, blacklist, progress, cancellationToken);
    }

    private async Task<IReadOnlyList<MarketScanItem>> EvaluatePricesAsync(
        IReadOnlyList<Gw2CommercePrice> rawPrices,
        MarketScanFilter filter,
        IReadOnlyCollection<string>? blacklist,
        IProgress<MarketScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var policy = new ProfitabilityPolicy(
            MinimumProfitPercentage: filter.MinimumProfitPercentage,
            MaximumBuyPriceCopper: filter.MaximumBuyPriceCopper);
        var overbid = Math.Max(0, filter.OverbidCopper);
        var undercut = Math.Max(0, filter.UndercutCopper);

        // Fetched (in ScanAsync) before matching so traded volume can filter and rank, not just decorate the results.
        var statsMap = _cachedStats;
        if (statsMap is null && NeedsStats(filter))
        {
            progress?.Report(new MarketScanProgress(rawPrices.Count, rawPrices.Count, 0, "Trade volume unavailable; ranking by spread and order depth instead."));
        }

        var spikesSkipped = 0;
        var matched = new List<(Gw2CommercePrice Price, ProfitabilityResult Eval, double LiquidityScore, double SortScore)>();
        foreach (var entry in rawPrices)
        {
            if (entry.Buys is null || entry.Sells is null) continue;
            MarketItemStats? stats = statsMap is null ? null : statsMap.GetValueOrDefault(entry.Id);
            double? tradesPerDay = statsMap is null ? null : stats?.TradesPerDay ?? 0;
            if (tradesPerDay is { } traded && filter.MinimumDailyTrades > 0 && traded < filter.MinimumDailyTrades) continue;
            if (filter.SkipPriceSpikes && IsPriceSpike(entry.Buys.UnitPrice, entry.Sells.UnitPrice, stats, filter.SpikeTolerance))
            {
                spikesSkipped++;
                continue;
            }

            var effectiveBuy = entry.Buys.UnitPrice + overbid;
            var effectiveSell = Math.Max(1L, entry.Sells.UnitPrice - undercut);
            if (effectiveBuy < filter.MinimumBuyPriceCopper) continue;
            if (entry.Sells.Quantity < filter.MinimumSupply) continue;
            if (entry.Buys.Quantity < filter.MinimumDemand) continue;

            var eval = _profitability.Evaluate(effectiveBuy, effectiveSell, policy);
            if (!eval.IsValid || !eval.IsProfitable) continue;
            if (eval.ProfitCopper < filter.MinimumProfitCopper) continue;
            if (filter.MaximumProfitPercentage.HasValue && eval.ProfitPercentage > filter.MaximumProfitPercentage.Value) continue;

            var minDepth = Math.Min(entry.Buys.Quantity, entry.Sells.Quantity);
            var liquidityScore = Math.Log10(1.0 + minDepth) * 25.0;
            var sortScore = ComputeSortScore(filter.SortMode, eval, entry, liquidityScore, tradesPerDay);
            matched.Add((entry, eval, liquidityScore, sortScore));
        }

        var hasRarityFilter = !string.IsNullOrWhiteSpace(filter.Rarity) &&
                              !filter.Rarity.Equals("All", StringComparison.OrdinalIgnoreCase);
        var hasTypeFilter = !string.IsNullOrWhiteSpace(filter.ItemType) &&
                            !filter.ItemType.Equals("All", StringComparison.OrdinalIgnoreCase);

        var multiplier = (hasRarityFilter ? 2 : 1) * (hasTypeFilter ? 3 : 1) * 2;
        var metadataLimit = Math.Max(filter.MaxResults, filter.MaxResults * multiplier);

        var topCandidates = matched
            .OrderByDescending(item => item.SortScore)
            .ThenByDescending(item => item.Eval.ProfitCopper)
            .Take(metadataLimit)
            .ToArray();

        if (topCandidates.Length == 0)
        {
            LastSpikesSkipped = spikesSkipped;
            progress?.Report(new MarketScanProgress(rawPrices.Count, rawPrices.Count, 0, "Scan complete: 0 items matched filters." + SpikeNote(spikesSkipped)));
            return Array.Empty<MarketScanItem>();
        }

        progress?.Report(new MarketScanProgress(rawPrices.Count, rawPrices.Count, topCandidates.Length, $"Resolving metadata for {topCandidates.Length} matches..."));
        var metadata = await FetchItemMetadataAsync(topCandidates.Select(item => item.Price.Id), cancellationToken).ConfigureAwait(false);
        var normalizedBlacklist = (blacklist ?? Array.Empty<string>())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(ItemNameNormalizer.Normalize)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var filteredRows = new List<(Gw2CommercePrice Price, ProfitabilityResult Eval, double LiquidityScore, double SortScore, Gw2CachedItemInfo Info)>();
        foreach (var candidate in topCandidates)
        {
            if (!metadata.TryGetValue(candidate.Price.Id, out var info) || string.IsNullOrWhiteSpace(info.Name))
            {
                continue;
            }

            if (normalizedBlacklist.Contains(ItemNameNormalizer.Normalize(info.Name)))
            {
                continue;
            }

            if (hasRarityFilter && !info.Rarity.Equals(filter.Rarity, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (hasTypeFilter && !info.ItemType.Equals(filter.ItemType, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            filteredRows.Add((candidate.Price, candidate.Eval, candidate.LiquidityScore, candidate.SortScore, info));
            if (filteredRows.Count >= filter.MaxResults)
            {
                break;
            }
        }

        Dictionary<int, Gw2ListingsBook>? orderBooks = null;
        if (filter.CheckWallDepth && filteredRows.Count > 0)
        {
            progress?.Report(new MarketScanProgress(rawPrices.Count, rawPrices.Count, filteredRows.Count, "Checking order-book wall depth..."));
            orderBooks = await FetchOrderBooksBestEffortAsync(filteredRows.Select(r => r.Price.Id), cancellationToken).ConfigureAwait(false);
        }

        var results = new List<MarketScanItem>(filteredRows.Count);
        foreach (var (price, eval, liquidityScore, _, info) in filteredRows)
        {
            Gw2ListingsBook? book = null;
            orderBooks?.TryGetValue(price.Id, out book);

            int? dailyVolume = null;
            if (statsMap is not null && statsMap.TryGetValue(price.Id, out var vol))
            {
                dailyVolume = (int)Math.Round(vol.TradesPerDay);
            }

            var riskBadge = ClassifyRiskBadge(price, eval, info, book);
            var tooltip = BuildTooltip(info, price, eval, riskBadge, dailyVolume);

            results.Add(new MarketScanItem(
                ItemId: price.Id,
                ItemName: info.Name,
                Rarity: info.Rarity,
                BuyPriceCopper: eval.BuyPriceCopper,
                SellPriceCopper: eval.SellPriceCopper,
                NetProfitCopper: eval.ProfitCopper,
                ProfitPercentage: decimal.Round(eval.ProfitPercentage, 2),
                BuyQuantity: price.Buys!.Quantity,
                SellQuantity: price.Sells!.Quantity,
                SuggestedQuantity: Math.Max(1, filter.DefaultQuantity),
                ItemType: info.ItemType,
                VendorValueCopper: info.VendorValueCopper,
                RiskBadge: riskBadge,
                FeeBreakdownTooltip: tooltip,
                DailyVolumeEstimate: dailyVolume,
                LiquidityScore: Math.Round(liquidityScore, 1)));
        }

        LastSpikesSkipped = spikesSkipped;
        progress?.Report(new MarketScanProgress(rawPrices.Count, rawPrices.Count, results.Count, $"Scan complete: {results.Count} profitable items found." + SpikeNote(spikesSkipped)));
        return results;
    }

    private static string SpikeNote(int spikesSkipped) =>
        spikesSkipped > 0 ? $" Skipped {spikesSkipped} price spike(s) vs the 7-day average." : string.Empty;

    private static bool NeedsStats(MarketScanFilter filter) =>
        filter.IncludeDataWarsVelocity ||
        filter.MinimumDailyTrades > 0 ||
        filter.SkipPriceSpikes ||
        string.Equals(filter.SortMode, MarketScanSortModes.GoldPerDay, StringComparison.Ordinal);

    /// <summary>
    /// Which item ids to price. With DataWars2 volume and a Min trades/day, every tradable item that trades often
    /// enough is priced (whole market, usually ~1-3k ids); otherwise the Scan scope samples the id list evenly.
    /// </summary>
    public static int[] SelectIdsToPrice(int[] allIds, MarketScanFilter filter, IReadOnlyDictionary<int, MarketItemStats>? stats)
    {
        if (stats is not null && filter.MinimumDailyTrades > 0)
        {
            return allIds.Where(id => stats.TryGetValue(id, out var s) && s.TradesPerDay >= filter.MinimumDailyTrades).ToArray();
        }

        return filter.MaxItemsToScan > 0 && filter.MaxItemsToScan < allIds.Length
            ? SampleEvenly(allIds, filter.MaxItemsToScan)
            : allIds;
    }

    /// <summary>True when the top bid or lowest ask sits more than <paramref name="tolerance"/> above its 7-day average.</summary>
    public static bool IsPriceSpike(long topBidCopper, long lowestAskCopper, MarketItemStats? stats, double tolerance)
    {
        if (stats is null)
        {
            return false;
        }

        var factor = 1.0 + Math.Max(0.05, tolerance);
        return (stats.SellAvg7dCopper is > 0 and var sellAvg && lowestAskCopper > sellAvg * factor) ||
               (stats.BuyAvg7dCopper is > 0 and var buyAvg && topBidCopper > buyAvg * factor);
    }

    public static async Task<IReadOnlyList<MarketScanItem>> ImportFromFileAsync(
        string filePath,
        ProfitabilityPolicy? policy = null,
        CancellationToken cancellationToken = default)
    {
        var candidates = await CandidatesLoader.LoadCandidatesAsync(filePath, cancellationToken).ConfigureAwait(false);
        var engine = new ProfitabilityEngine();
        var effectivePolicy = policy ?? new ProfitabilityPolicy();
        var imported = new List<MarketScanItem>(candidates.Count);

        var syntheticId = 1;
        foreach (var candidate in candidates)
        {
            var buy = candidate.BuyPriceCopper ?? 0;
            var sell = candidate.SellPriceCopper ?? 0;
            long netProfit = 0;
            decimal roi = candidate.Roi ?? 0m;
            if (buy > 0 && sell > 0)
            {
                var eval = engine.Evaluate(buy, sell, effectivePolicy);
                if (eval.IsValid)
                {
                    netProfit = eval.ProfitCopper;
                    roi = decimal.Round(eval.ProfitPercentage, 2);
                }
            }

            imported.Add(new MarketScanItem(
                ItemId: syntheticId++,
                ItemName: candidate.ItemName,
                Rarity: string.IsNullOrWhiteSpace(candidate.Rarity) ? "Unknown" : candidate.Rarity,
                BuyPriceCopper: buy,
                SellPriceCopper: sell,
                NetProfitCopper: netProfit,
                ProfitPercentage: roi,
                BuyQuantity: 0,
                SellQuantity: 0,
                SuggestedQuantity: Math.Max(1, candidate.Quantity ?? 1),
                RiskBadge: "Imported"));
        }

        return imported;
    }

    internal static int[] SampleEvenly(int[] allIds, int targetCount)
    {
        if (targetCount >= allIds.Length)
        {
            return allIds;
        }

        var sampled = new int[targetCount];
        var step = (double)allIds.Length / targetCount;
        for (var i = 0; i < targetCount; i++)
        {
            sampled[i] = allIds[(int)(i * step)];
        }

        return sampled;
    }

    private static double ComputeSortScore(
        string? sortMode,
        ProfitabilityResult eval,
        Gw2CommercePrice entry,
        double liquidityScore,
        double? tradesPerDay = null)
    {
        return sortMode switch
        {
            // Without volume data this falls back to the balanced spread/depth score below.
            MarketScanSortModes.GoldPerDay when tradesPerDay is { } traded =>
                (double)eval.ProfitCopper * Math.Min(traded, MarketScanSortModes.TradesPerDayCap),
            "Roi" => (double)eval.ProfitPercentage,
            "Liquidity" => Math.Min(entry.Buys!.Quantity, entry.Sells!.Quantity),
            "NetProfit" => eval.ProfitCopper,
            _ => (double)eval.ProfitCopper *
                 (1.0 + liquidityScore / 50.0) *
                 Math.Clamp((double)eval.ProfitPercentage, 1.0, 60.0) / 25.0
        };
    }

    private static string ClassifyRiskBadge(
        Gw2CommercePrice price,
        ProfitabilityResult eval,
        Gw2CachedItemInfo info,
        Gw2ListingsBook? book)
    {
        var buyQty = price.Buys?.Quantity ?? 0;
        var sellQty = price.Sells?.Quantity ?? 0;

        if (book is not null)
        {
            var buys = book.Buys ?? [];
            var sells = book.Sells ?? [];
            if (sells.Count >= 2 && sells[0].Quantity <= 5 &&
                sells[0].UnitPrice > 0 &&
                (sells[1].UnitPrice - sells[0].UnitPrice) > sells[0].UnitPrice * 0.20m)
            {
                return "Wide Spread";
            }

            if (sells.Count >= 1 &&
                sells[0].Quantity >= Math.Max(500, buyQty * 3) &&
                sells[0].Listings <= 3)
            {
                return "Sell Wall";
            }

            if (buys.Count < 3 || sells.Count < 3)
            {
                return "Thin Book";
            }
        }

        if (info.VendorValueCopper > 0 && eval.BuyPriceCopper <= info.VendorValueCopper)
        {
            return "Near Vendor";
        }

        if (eval.ProfitPercentage >= 95m)
        {
            return "Wide Spread";
        }

        if (buyQty >= 250 && sellQty >= 250)
        {
            return "Liquid";
        }

        if (buyQty < 30 || sellQty < 30)
        {
            return "Thin Book";
        }

        return "Balanced";
    }

    private static string BuildTooltip(
        Gw2CachedItemInfo info,
        Gw2CommercePrice price,
        ProfitabilityResult eval,
        string riskBadge,
        int? dailyVolume)
    {
        var typePart = string.IsNullOrWhiteSpace(info.ItemType) ? info.Rarity : $"{info.Rarity} {info.ItemType}";
        var volPart = dailyVolume.HasValue ? $" • Trades: ~{dailyVolume.Value:N0}/day (7-day avg, DataWars2)" : string.Empty;
        return $"{info.Name} ({typePart})\n" +
               $"Buy: {MarketScanItem.FormatCoins(eval.BuyPriceCopper)}  •  Sell: {MarketScanItem.FormatCoins(eval.SellPriceCopper)}\n" +
               $"Fees: -{MarketScanItem.FormatCoins(eval.ListingFeeCopper)} (5% list)  -{MarketScanItem.FormatCoins(eval.ExchangeFeeCopper)} (10% tax)\n" +
               $"Net Profit: {MarketScanItem.FormatCoins(eval.ProfitCopper)} ({eval.ProfitPercentage:0.0}% ROI)\n" +
               $"Depth: {price.Buys!.Quantity:N0} demand / {price.Sells!.Quantity:N0} supply{volPart}\n" +
               $"Signal: {riskBadge}";
    }

    private async Task<Dictionary<int, Gw2CachedItemInfo>> FetchItemMetadataAsync(
        IEnumerable<int> itemIds,
        CancellationToken cancellationToken)
    {
        var distinctIds = itemIds.Distinct().ToArray();
        Dictionary<int, Gw2CachedItemInfo> map;

        if (_itemCache is not null)
        {
            await EnsureCacheInitializedAsync(cancellationToken).ConfigureAwait(false);
            map = await _itemCache.GetCachedItemsAsync(distinctIds, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            map = new Dictionary<int, Gw2CachedItemInfo>();
        }

        var missing = distinctIds.Where(id => !map.ContainsKey(id)).ToArray();
        if (missing.Length == 0)
        {
            return map;
        }

        var newlyFetched = new List<Gw2CachedItemInfo>();
        var now = DateTimeOffset.UtcNow;
        foreach (var chunk in missing.Chunk(ApiBatchSize))
        {
            var ids = string.Join(',', chunk);
            var items = await GetJsonAsync<List<Gw2ItemMetadata>>($"v2/items?ids={ids}", cancellationToken).ConfigureAwait(false);
            foreach (var item in items)
            {
                if (string.IsNullOrWhiteSpace(item.Name)) continue;
                var info = new Gw2CachedItemInfo(
                    ItemId: item.Id,
                    Name: item.Name.Trim(),
                    Rarity: string.IsNullOrWhiteSpace(item.Rarity) ? "Basic" : item.Rarity.Trim(),
                    ItemType: item.Type?.Trim() ?? string.Empty,
                    VendorValueCopper: item.VendorValue,
                    UpdatedAt: now);
                map[item.Id] = info;
                newlyFetched.Add(info);
            }
        }

        if (_itemCache is not null && newlyFetched.Count > 0)
        {
            await _itemCache.UpsertItemsAsync(newlyFetched, cancellationToken).ConfigureAwait(false);
        }

        return map;
    }

    private async Task EnsureCacheInitializedAsync(CancellationToken cancellationToken)
    {
        if (_cacheInitialized || _itemCache is null)
        {
            return;
        }

        await _cacheInitGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_cacheInitialized)
            {
                await _itemCache.InitializeAsync(cancellationToken).ConfigureAwait(false);
                _cacheInitialized = true;
            }
        }
        finally
        {
            _cacheInitGate.Release();
        }
    }

    private async Task<Dictionary<int, Gw2ListingsBook>> FetchOrderBooksBestEffortAsync(
        IEnumerable<int> itemIds,
        CancellationToken cancellationToken)
    {
        var map = new Dictionary<int, Gw2ListingsBook>();
        foreach (var chunk in itemIds.Distinct().Chunk(ApiBatchSize))
        {
            try
            {
                var ids = string.Join(',', chunk);
                var books = await GetJsonAsync<List<Gw2ListingsBook>>($"v2/commerce/listings?ids={ids}", cancellationToken).ConfigureAwait(false);
                foreach (var book in books)
                {
                    map[book.Id] = book;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Keep scan resilient if listings endpoint hiccups on a batch.
            }
        }

        return map;
    }

    private async Task<Dictionary<int, MarketItemStats>?> FetchDataWarsStatsBestEffortAsync(CancellationToken cancellationToken)
    {
        try
        {
            // DataWars2 streams ~1.8 MB slowly: uncompressed it took 120 s+ and hit the old 30 s timeout
            // (the scan then silently fell back to sampling). Compressed it is ~0.4 MB in ~20 s.
            using var ownedClient = _velocityHttpClient is null
                ? new HttpClient(new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All })
                {
                    Timeout = TimeSpan.FromSeconds(90)
                }
                : null;
            var client = _velocityHttpClient ?? ownedClient!;
            using var response = await client.GetAsync(
                "https://api.datawars2.ie/gw2/v1/items/json?fields=id,1d_buy_sold,1d_sell_sold,7d_buy_sold,7d_sell_sold,7d_buy_price_avg,7d_sell_price_avg",
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            return ParseDataWarsVelocity(doc.RootElement);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Trades per day per item: min(units sold into buy orders, units bought from listings), as a 7-day daily
    /// average (falls back to the last day). "buy_sold" is how fast our buy orders can fill; "sell_sold" how fast our
    /// listings can sell. A flip needs both, so the smaller one is the item's real throughput.
    /// </summary>
    internal static Dictionary<int, MarketItemStats>? ParseDataWarsVelocity(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        static double Read(JsonElement element, string field) =>
            element.TryGetProperty(field, out var prop) && prop.ValueKind == JsonValueKind.Number && prop.TryGetDouble(out var value)
                ? Math.Max(0, value)
                : -1;

        var map = new Dictionary<int, MarketItemStats>();
        foreach (var elem in root.EnumerateArray())
        {
            if (!elem.TryGetProperty("id", out var idProp) || !idProp.TryGetInt32(out var id))
            {
                continue;
            }

            var weekBuy = Read(elem, "7d_buy_sold");
            var weekSell = Read(elem, "7d_sell_sold");
            double perDay;
            if (weekBuy >= 0 && weekSell >= 0)
            {
                perDay = Math.Min(weekBuy, weekSell) / 7.0;
            }
            else
            {
                var dayBuy = Read(elem, "1d_buy_sold");
                var daySell = Read(elem, "1d_sell_sold");
                if (dayBuy < 0 || daySell < 0) continue;
                perDay = Math.Min(dayBuy, daySell);
            }

            var buyAvg = Read(elem, "7d_buy_price_avg");
            var sellAvg = Read(elem, "7d_sell_price_avg");
            map[id] = new MarketItemStats(
                perDay,
                buyAvg > 0 ? (long)Math.Round(buyAvg) : null,
                sellAvg > 0 ? (long)Math.Round(sellAvg) : null);
        }

        return map;
    }

    private async Task<T> GetJsonAsync<T>(string relativeUrl, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(relativeUrl, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
               ?? throw new InvalidDataException($"GW2 API returned empty payload for '{relativeUrl}'.");
    }

    private sealed record Gw2CommercePrice(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("buys")] Gw2PriceSide? Buys,
        [property: JsonPropertyName("sells")] Gw2PriceSide? Sells);

    private sealed record Gw2PriceSide(
        [property: JsonPropertyName("quantity")] long Quantity,
        [property: JsonPropertyName("unit_price")] long UnitPrice);

    private sealed record Gw2ItemMetadata(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("rarity")] string? Rarity,
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("vendor_value")] long VendorValue);

    private sealed record Gw2ListingsBook(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("buys")] List<Gw2ListingLevel>? Buys,
        [property: JsonPropertyName("sells")] List<Gw2ListingLevel>? Sells);

    private sealed record Gw2ListingLevel(
        [property: JsonPropertyName("listings")] int Listings,
        [property: JsonPropertyName("unit_price")] long UnitPrice,
        [property: JsonPropertyName("quantity")] long Quantity);
}
