namespace Mmopal.Gw2;

// Rows and totals of the Income & Portfolio report.
public sealed record DailyProfitPoint(
    DateOnly Date,
    long ProfitCopper,
    long CumulativeProfitCopper,
    int ClosedFlips,
    string Label,
    string Tooltip);

public sealed record HourlyActivityPoint(
    int LocalHour,
    int BuyFills,
    int SellFills,
    long RealizedProfitCopper,
    string HourLabel,
    string Tooltip);

public sealed record ItemPnlRow(
    int ItemId,
    string ItemName,
    long UnitsFlipped,
    long AvgBuyCopper,
    long AvgSellCopper,
    long NetProfitCopper,
    decimal RoiPercent,
    double AvgHoldHours,
    string OutcomeBadge)
{
    public string AvgBuyFormatted => MarketScanItem.FormatCoins(AvgBuyCopper);
    public string AvgSellFormatted => MarketScanItem.FormatCoins(AvgSellCopper);
    public string NetProfitFormatted => MarketScanItem.FormatCoins(NetProfitCopper);
    public string RoiFormatted => $"{RoiPercent:0.0}%";
    public string AvgHoldFormatted
    {
        get
        {
            if (AvgHoldHours < 1.0) return $"{Math.Round(AvgHoldHours * 60):0}m";
            if (AvgHoldHours < 48.0) return $"{AvgHoldHours:0.0}h";
            return $"{AvgHoldHours / 24.0:0.0}d";
        }
    }
}

public sealed record TradingIncomeReport(
    long MatchedProfitCopper,
    long MatchedCostBasisCopper,
    long MatchedNetRevenueCopper,
    decimal MatchedRoiPercent,
    long UnmatchedSellRevenueCopper,
    long TotalBuysSpentCopper,
    long MatchedUnitsSold,
    long UnmatchedUnitsSold,
    double WinRatePercent,
    double AvgProfitPerActiveDayCopper,
    IReadOnlyList<DailyProfitPoint> DailySeries,
    IReadOnlyList<HourlyActivityPoint> HourlySeries,
    IReadOnlyList<ItemPnlRow> ItemLeaderboard);
