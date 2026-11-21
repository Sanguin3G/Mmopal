using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mmopal.Contracts;
using Mmopal.Infrastructure;

namespace Mmopal.Gw2;

// Market Scanner inputs and results: filters, sort modes, per-item stats, progress, result rows, and the scanner interface.
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
