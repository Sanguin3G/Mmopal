using System.Globalization;
using System.Text;
using Mmopal.Contracts;

namespace Mmopal.Gw2;

public sealed record Gw2MarketCycleInfo(
    string PhaseName,
    string Advice,
    TimeSpan TimeUntilReset,
    DateTimeOffset NextResetLocal,
    string Summary);

public static class Gw2MarketCycle
{
    public static Gw2MarketCycleInfo Describe(DateTimeOffset nowUtc, TimeZoneInfo? localTimeZone = null)
    {
        var utc = nowUtc.ToUniversalTime();
        var tz = localTimeZone ?? TimeZoneInfo.Local;
        var nextResetUtc = new DateTimeOffset(utc.Year, utc.Month, utc.Day, 0, 0, 0, TimeSpan.Zero).AddDays(1);
        var untilReset = nextResetUtc - utc;
        var nextResetLocal = TimeZoneInfo.ConvertTime(nextResetUtc, tz);
        var localNow = TimeZoneInfo.ConvertTime(utc, tz);
        var tod = utc.TimeOfDay;

        string phase;
        string advice;
        if (tod < TimeSpan.FromMinutes(90))
        {
            phase = "Daily Reset Spike";
            advice = "High insta-sell wave into Buy Orders after 00:00 UTC reset.";
        }
        else if (tod >= TimeSpan.FromHours(17) && tod < TimeSpan.FromHours(22))
        {
            phase = "EU Prime Time";
            advice = "Peak EU raids & crafting; strong sell-side velocity.";
        }
        else if (tod >= TimeSpan.FromHours(22) || tod < TimeSpan.FromHours(4))
        {
            phase = "NA Prime Time";
            advice = "Peak NA crafting & market turnover across shared NA/EU TP.";
        }
        else
        {
            phase = "Global Off-Peak";
            advice = "Lower volume; avoid panic-canceling sells and losing 5% listing fees.";
        }

        var hours = (int)untilReset.TotalHours;
        var mins = untilReset.Minutes;
        var summary = $"{phase} ({localNow:HH:mm}) • Reset in {hours}h {mins:00}m ({nextResetLocal:HH:mm} local)";
        return new Gw2MarketCycleInfo(phase, advice, untilReset, nextResetLocal, summary);
    }
}

public sealed record TradingStrategyPreset(
    string Name,
    string Description,
    decimal MinRoiPercent,
    decimal MaxRoiPercent,
    long MinProfitSilver,
    long MinBuySilver,
    decimal MaxBuyGold,
    long MinSupply,
    long MinDemand,
    string PreferredCategory,
    decimal RecommendedGoldFloor,
    long DefaultOrderQuantity,
    int OverbidMinCopper,
    int OverbidMaxCopper,
    int UndercutMinCopper,
    int UndercutMaxCopper,
    double MaxSessionHours,
    double SwapCharacterHours,
    string Rationale);

public static class TradingStrategyPresets
{
    public static IReadOnlyList<TradingStrategyPreset> All { get; } =
    [
        new TradingStrategyPreset(
            Name: "Starter (< 100g)",
            Description: "Low-cost, fast-turning flips safe for small bankrolls.",
            MinRoiPercent: 10m,
            MaxRoiPercent: 60m,
            MinProfitSilver: 2,
            MinBuySilver: 5,
            MaxBuyGold: 3m,
            MinSupply: 250,
            MinDemand: 250,
            PreferredCategory: "All",
            RecommendedGoldFloor: 15m,
            DefaultOrderQuantity: 5,
            OverbidMinCopper: 1,
            OverbidMaxCopper: 2,
            UndercutMinCopper: 1,
            UndercutMaxCopper: 2,
            MaxSessionHours: 10,
            SwapCharacterHours: 2.5,
            Rationale: "Prioritizes 10–60% ROI items under 3g with 250+ supply/demand so capital rotates multiple times per night."),
        new TradingStrategyPreset(
            Name: "Balanced (100g–1,000g)",
            Description: "Diversified mid-tier flips balancing margin and turnover.",
            MinRoiPercent: 10m,
            MaxRoiPercent: 85m,
            MinProfitSilver: 10,
            MinBuySilver: 20,
            MaxBuyGold: 20m,
            MinSupply: 100,
            MinDemand: 100,
            PreferredCategory: "All",
            RecommendedGoldFloor: 100m,
            DefaultOrderQuantity: 2,
            OverbidMinCopper: 1,
            OverbidMaxCopper: 3,
            UndercutMinCopper: 1,
            UndercutMaxCopper: 3,
            MaxSessionHours: 10,
            SwapCharacterHours: 2.5,
            Rationale: "Caps single-item exposure around 20g, demands 100+ open depth, and keeps a coin reserve for 5% listing fees."),
        new TradingStrategyPreset(
            Name: "High Capital (1,000g+)",
            Description: "Higher unit-profit flips for deep bankrolls.",
            MinRoiPercent: 12m,
            MaxRoiPercent: 110m,
            MinProfitSilver: 50,
            MinBuySilver: 100,
            MaxBuyGold: 75m,
            MinSupply: 30,
            MinDemand: 30,
            PreferredCategory: "All",
            RecommendedGoldFloor: 250m,
            DefaultOrderQuantity: 1,
            OverbidMinCopper: 1,
            OverbidMaxCopper: 5,
            UndercutMinCopper: 1,
            UndercutMaxCopper: 5,
            MaxSessionHours: 10,
            SwapCharacterHours: 2.5,
            Rationale: "Targets 50s+ profit per unit while capping exposure per item and keeping ROI realistic against wide-spread traps."),
        new TradingStrategyPreset(
            Name: "High-Volume Safe",
            Description: "Deep liquidity staples that fill reliably during EU/NA peaks.",
            MinRoiPercent: 8m,
            MaxRoiPercent: 30m,
            MinProfitSilver: 2,
            MinBuySilver: 10,
            MaxBuyGold: 10m,
            MinSupply: 500,
            MinDemand: 500,
            PreferredCategory: "CraftingMaterial",
            RecommendedGoldFloor: 50m,
            DefaultOrderQuantity: 10,
            OverbidMinCopper: 1,
            OverbidMaxCopper: 2,
            UndercutMinCopper: 1,
            UndercutMaxCopper: 2,
            MaxSessionHours: 10,
            SwapCharacterHours: 2.5,
            Rationale: "Focuses on high-velocity crafting commodities (500+ depth) with tight spreads that resist stagnation.")
    ];

    public static TradingStrategyPreset? FindByName(string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? null
            : All.FirstOrDefault(p => p.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));

    public static TradingStrategyPreset RecommendForCapital(long totalCapitalCopper)
    {
        var gold = Math.Max(0m, totalCapitalCopper / 10_000m);
        if (gold < 100m)
        {
            var basePreset = All[0];
            var maxBuy = Math.Clamp(Math.Round(gold * 0.05m, 1), 1m, 5m);
            var floor = Math.Round(gold * 0.15m, 1);
            return basePreset with
            {
                MaxBuyGold = maxBuy,
                RecommendedGoldFloor = floor,
                Rationale = $"Wallet ~{gold:0.#}g (Starter tier): Max Buy {maxBuy:0.#}g (~5% cap/item) and {floor:0.#}g reserve for listing fees."
            };
        }

        if (gold < 1_000m)
        {
            var basePreset = All[1];
            var maxBuy = Math.Clamp(Math.Round(gold * 0.04m, 0), 5m, 35m);
            var floor = Math.Round(gold * 0.15m, 0);
            return basePreset with
            {
                MaxBuyGold = maxBuy,
                RecommendedGoldFloor = floor,
                Rationale = $"Wallet ~{gold:0}g (Balanced tier): Max Buy {maxBuy:0}g (~4% cap/item) and {floor:0}g reserve for listing fees."
            };
        }

        var highPreset = All[2];
        var highMaxBuy = Math.Clamp(Math.Round(gold * 0.04m, 0), 35m, 150m);
        var highFloor = Math.Round(gold * 0.15m, 0);
        return highPreset with
        {
            MaxBuyGold = highMaxBuy,
            RecommendedGoldFloor = highFloor,
            Rationale = $"Wallet ~{gold:0}g (High-Capital tier): Max Buy {highMaxBuy:0}g (~4% cap/item) and {highFloor:0}g reserve."
        };
    }
}

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
    public string AvgHoldFormatted => AvgHoldHours < 1.0
        ? $"{Math.Round(AvgHoldHours * 60):0}m"
        : AvgHoldHours < 48.0
            ? $"{AvgHoldHours:0.0}h"
            : $"{AvgHoldHours / 24.0:0.0}d";
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

public static class TradingIncomeAnalytics
{
    public static long CalculateListingFee(long sellUnitPriceCopper) =>
        sellUnitPriceCopper <= 0
            ? 0
            : Math.Max(1L, (long)Math.Floor(sellUnitPriceCopper * 0.05m + 0.5m));

    public static long CalculateExchangeFee(long sellUnitPriceCopper) =>
        sellUnitPriceCopper <= 0
            ? 0
            : Math.Max(1L, (long)Math.Floor(sellUnitPriceCopper * 0.10m + 0.5m));

    public static long CalculateNetSaleUnitPrice(long sellUnitPriceCopper) =>
        sellUnitPriceCopper <= 0
            ? 0
            : sellUnitPriceCopper - CalculateListingFee(sellUnitPriceCopper) - CalculateExchangeFee(sellUnitPriceCopper);

    public static TradingIncomeReport Analyze(
        IEnumerable<Gw2LedgerTransaction> allHistory,
        DateTimeOffset? windowStartUtc = null,
        TimeZoneInfo? localTimeZone = null)
    {
        ArgumentNullException.ThrowIfNull(allHistory);
        var tz = localTimeZone ?? TimeZoneInfo.Local;
        var chronological = allHistory
            .OrderBy(t => t.PurchasedAt)
            .ThenBy(t => t.Id)
            .ToArray();

        var queues = new Dictionary<int, Queue<BuyLot>>();
        long matchedProfitCopper = 0;
        long matchedCostBasisCopper = 0;
        long matchedNetRevenueCopper = 0;
        long unmatchedSellRevenueCopper = 0;
        long totalBuysSpentCopper = 0;
        long matchedUnitsSold = 0;
        long unmatchedUnitsSold = 0;
        var winningSales = 0;
        var totalMatchedSaleEvents = 0;

        var dailyMap = new Dictionary<DateOnly, DailyAccumulator>();
        var hourlyBuyFills = new int[24];
        var hourlySellFills = new int[24];
        var hourlyProfit = new long[24];
        var itemMap = new Dictionary<int, ItemAccumulator>();

        foreach (var tx in chronological)
        {
            if (tx.Quantity <= 0 || tx.UnitPriceCopper < 0)
            {
                continue;
            }

            var inWindow = !windowStartUtc.HasValue || tx.PurchasedAt >= windowStartUtc.Value;
            var localTime = TimeZoneInfo.ConvertTime(tx.PurchasedAt, tz);

            if (tx.Kind == TransactionType.Buy)
            {
                if (!queues.TryGetValue(tx.ItemId, out var q))
                {
                    q = new Queue<BuyLot>();
                    queues[tx.ItemId] = q;
                }

                q.Enqueue(new BuyLot(tx.Quantity, tx.UnitPriceCopper, tx.PurchasedAt));
                if (inWindow)
                {
                    totalBuysSpentCopper += tx.UnitPriceCopper * tx.Quantity;
                    hourlyBuyFills[localTime.Hour]++;
                }

                continue;
            }

            // Sell transaction: consume FIFO buy lots for this item ID.
            var netSaleUnit = CalculateNetSaleUnitPrice(tx.UnitPriceCopper);
            long remainingToMatch = tx.Quantity;
            long matchedQty = 0;
            long matchedCost = 0;
            double weightedHoldHours = 0;

            if (queues.TryGetValue(tx.ItemId, out var buyQueue))
            {
                while (remainingToMatch > 0 && buyQueue.Count > 0)
                {
                    var lot = buyQueue.Peek();
                    var take = Math.Min(remainingToMatch, lot.RemainingQuantity);
                    matchedQty += take;
                    matchedCost += take * lot.UnitPriceCopper;
                    var holdHours = Math.Max(0.0, (tx.PurchasedAt - lot.PurchasedAt).TotalHours);
                    weightedHoldHours += holdHours * take;

                    lot.RemainingQuantity -= take;
                    remainingToMatch -= take;
                    if (lot.RemainingQuantity <= 0)
                    {
                        buyQueue.Dequeue();
                    }
                }
            }

            var unmatchedQty = remainingToMatch;
            if (!inWindow)
            {
                continue;
            }

            hourlySellFills[localTime.Hour]++;

            if (unmatchedQty > 0)
            {
                unmatchedUnitsSold += unmatchedQty;
                unmatchedSellRevenueCopper += netSaleUnit * unmatchedQty;
            }

            if (matchedQty <= 0)
            {
                continue;
            }

            var matchedRevenue = netSaleUnit * matchedQty;
            var flipProfit = matchedRevenue - matchedCost;

            matchedUnitsSold += matchedQty;
            matchedCostBasisCopper += matchedCost;
            matchedNetRevenueCopper += matchedRevenue;
            matchedProfitCopper += flipProfit;
            totalMatchedSaleEvents++;
            if (flipProfit > 0)
            {
                winningSales++;
            }

            hourlyProfit[localTime.Hour] += flipProfit;

            var localDate = DateOnly.FromDateTime(localTime.DateTime);
            if (!dailyMap.TryGetValue(localDate, out var dayAcc))
            {
                dayAcc = new DailyAccumulator();
                dailyMap[localDate] = dayAcc;
            }

            dayAcc.ProfitCopper += flipProfit;
            dayAcc.ClosedFlips++;

            if (!itemMap.TryGetValue(tx.ItemId, out var itemAcc))
            {
                itemAcc = new ItemAccumulator(tx.ItemId, string.IsNullOrWhiteSpace(tx.ItemName) ? $"Item #{tx.ItemId}" : tx.ItemName);
                itemMap[tx.ItemId] = itemAcc;
            }
            else if (!string.IsNullOrWhiteSpace(tx.ItemName))
            {
                itemAcc.ItemName = tx.ItemName;
            }

            itemAcc.UnitsFlipped += matchedQty;
            itemAcc.TotalBuyCostCopper += matchedCost;
            itemAcc.TotalGrossSellCopper += tx.UnitPriceCopper * matchedQty;
            itemAcc.TotalNetProfitCopper += flipProfit;
            itemAcc.TotalWeightedHoldHours += weightedHoldHours;
        }

        var dailySeries = BuildDailySeries(dailyMap);
        var hourlySeries = new List<HourlyActivityPoint>(24);
        for (var h = 0; h < 24; h++)
        {
            var buys = hourlyBuyFills[h];
            var sells = hourlySellFills[h];
            var prof = hourlyProfit[h];
            hourlySeries.Add(new HourlyActivityPoint(
                LocalHour: h,
                BuyFills: buys,
                SellFills: sells,
                RealizedProfitCopper: prof,
                HourLabel: $"{h:00}",
                Tooltip: $"{h:00}:00–{h:00}:59 • Buys: {buys} • Sells: {sells} • Net Profit: {MarketScanItem.FormatCoins(prof)}"));
        }

        var leaderboard = itemMap.Values
            .Select(acc =>
            {
                var avgBuy = acc.UnitsFlipped > 0 ? acc.TotalBuyCostCopper / acc.UnitsFlipped : 0;
                var avgSell = acc.UnitsFlipped > 0 ? acc.TotalGrossSellCopper / acc.UnitsFlipped : 0;
                var roi = acc.TotalBuyCostCopper > 0
                    ? decimal.Round(acc.TotalNetProfitCopper * 100m / acc.TotalBuyCostCopper, 2)
                    : 0m;
                var avgHold = acc.UnitsFlipped > 0 ? acc.TotalWeightedHoldHours / acc.UnitsFlipped : 0.0;
                var badge = acc.TotalNetProfitCopper > 0 ? "Winner" : acc.TotalNetProfitCopper == 0 ? "Break-even" : "Loss";
                return new ItemPnlRow(
                    acc.ItemId,
                    acc.ItemName,
                    acc.UnitsFlipped,
                    avgBuy,
                    avgSell,
                    acc.TotalNetProfitCopper,
                    roi,
                    avgHold,
                    badge);
            })
            .OrderByDescending(row => row.NetProfitCopper)
            .ThenByDescending(row => row.UnitsFlipped)
            .ToArray();

        var matchedRoi = matchedCostBasisCopper > 0
            ? decimal.Round(matchedProfitCopper * 100m / matchedCostBasisCopper, 2)
            : 0m;
        var winRate = totalMatchedSaleEvents > 0
            ? winningSales * 100.0 / totalMatchedSaleEvents
            : 0.0;
        var avgDaily = dailyMap.Count > 0
            ? (double)matchedProfitCopper / dailyMap.Count
            : 0.0;

        return new TradingIncomeReport(
            MatchedProfitCopper: matchedProfitCopper,
            MatchedCostBasisCopper: matchedCostBasisCopper,
            MatchedNetRevenueCopper: matchedNetRevenueCopper,
            MatchedRoiPercent: matchedRoi,
            UnmatchedSellRevenueCopper: unmatchedSellRevenueCopper,
            TotalBuysSpentCopper: totalBuysSpentCopper,
            MatchedUnitsSold: matchedUnitsSold,
            UnmatchedUnitsSold: unmatchedUnitsSold,
            WinRatePercent: winRate,
            AvgProfitPerActiveDayCopper: avgDaily,
            DailySeries: dailySeries,
            HourlySeries: hourlySeries,
            ItemLeaderboard: leaderboard);
    }

    public static void ExportLedgerCsv(string path, IEnumerable<Gw2LedgerTransaction> rows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(rows);
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        var sb = new StringBuilder();
        sb.AppendLine("Id;Kind;ItemId;ItemName;UnitPriceCopper;Quantity;TotalGrossCopper;CreatedAt;PurchasedAt");
        foreach (var row in rows.OrderByDescending(r => r.PurchasedAt).ThenByDescending(r => r.Id))
        {
            var safeName = row.ItemName.Contains(';') || row.ItemName.Contains('"')
                ? $"\"{row.ItemName.Replace("\"", "\"\"")}\""
                : row.ItemName;
            sb.Append(row.Id).Append(';')
              .Append(row.Kind).Append(';')
              .Append(row.ItemId).Append(';')
              .Append(safeName).Append(';')
              .Append(row.UnitPriceCopper).Append(';')
              .Append(row.Quantity).Append(';')
              .Append(row.UnitPriceCopper * row.Quantity).Append(';')
              .Append(row.CreatedAt.ToString("O", CultureInfo.InvariantCulture)).Append(';')
              .Append(row.PurchasedAt.ToString("O", CultureInfo.InvariantCulture))
              .AppendLine();
        }

        File.WriteAllText(full, sb.ToString());
    }

    private static List<DailyProfitPoint> BuildDailySeries(Dictionary<DateOnly, DailyAccumulator> dailyMap)
    {
        var points = new List<DailyProfitPoint>();
        if (dailyMap.Count == 0)
        {
            return points;
        }

        var minDate = dailyMap.Keys.Min();
        var maxDate = dailyMap.Keys.Max();
        var spanDays = maxDate.DayNumber - minDate.DayNumber;
        long cumulative = 0;

        if (spanDays <= 45)
        {
            for (var day = minDate; day <= maxDate; day = day.AddDays(1))
            {
                dailyMap.TryGetValue(day, out var acc);
                var profit = acc?.ProfitCopper ?? 0;
                var flips = acc?.ClosedFlips ?? 0;
                cumulative += profit;
                points.Add(new DailyProfitPoint(
                    day,
                    profit,
                    cumulative,
                    flips,
                    day.ToString("MM-dd", CultureInfo.InvariantCulture),
                    $"{day:yyyy-MM-dd} • Net: {MarketScanItem.FormatCoins(profit)} • Cumulative: {MarketScanItem.FormatCoins(cumulative)} ({flips} sells)"));
            }
        }
        else
        {
            foreach (var day in dailyMap.Keys.OrderBy(d => d))
            {
                var acc = dailyMap[day];
                cumulative += acc.ProfitCopper;
                points.Add(new DailyProfitPoint(
                    day,
                    acc.ProfitCopper,
                    cumulative,
                    acc.ClosedFlips,
                    day.ToString("MM-dd", CultureInfo.InvariantCulture),
                    $"{day:yyyy-MM-dd} • Net: {MarketScanItem.FormatCoins(acc.ProfitCopper)} • Cumulative: {MarketScanItem.FormatCoins(cumulative)} ({acc.ClosedFlips} sells)"));
            }
        }

        return points;
    }

    private sealed class BuyLot(long quantity, long unitPriceCopper, DateTimeOffset purchasedAt)
    {
        public long RemainingQuantity { get; set; } = quantity;
        public long UnitPriceCopper { get; } = unitPriceCopper;
        public DateTimeOffset PurchasedAt { get; } = purchasedAt;
    }

    private sealed class DailyAccumulator
    {
        public long ProfitCopper { get; set; }
        public int ClosedFlips { get; set; }
    }

    private sealed class ItemAccumulator(int itemId, string itemName)
    {
        public int ItemId { get; } = itemId;
        public string ItemName { get; set; } = itemName;
        public long UnitsFlipped { get; set; }
        public long TotalBuyCostCopper { get; set; }
        public long TotalGrossSellCopper { get; set; }
        public long TotalNetProfitCopper { get; set; }
        public double TotalWeightedHoldHours { get; set; }
    }
}
