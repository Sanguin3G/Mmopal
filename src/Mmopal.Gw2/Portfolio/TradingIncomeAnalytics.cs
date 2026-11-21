using System.Globalization;
using System.Text;
using Mmopal.Contracts;

namespace Mmopal.Gw2;

public static class TradingIncomeAnalytics
{
    public static long CalculateListingFee(long sellUnitPriceCopper) => RoundedFee(sellUnitPriceCopper, 0.05m);

    public static long CalculateExchangeFee(long sellUnitPriceCopper) => RoundedFee(sellUnitPriceCopper, 0.10m);

    /// <summary>The fee rounded half up, and at least 1c on any positive price.</summary>
    private static long RoundedFee(long sellUnitPriceCopper, decimal rate) =>
        sellUnitPriceCopper <= 0
            ? 0
            : Math.Max(1L, (long)Math.Floor(sellUnitPriceCopper * rate + 0.5m));

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
                GetOrAdd(queues, tx.ItemId).Enqueue(new BuyLot(tx.Quantity, tx.UnitPriceCopper, tx.PurchasedAt));
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

            var dayAcc = GetOrAdd(dailyMap, DateOnly.FromDateTime(localTime.DateTime));
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
                return new ItemPnlRow(
                    acc.ItemId,
                    acc.ItemName,
                    acc.UnitsFlipped,
                    avgBuy,
                    avgSell,
                    acc.TotalNetProfitCopper,
                    roi,
                    avgHold,
                    OutcomeBadge(acc.TotalNetProfitCopper));
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
        // Up to 45 days every calendar day gets a point (idle days at 0); longer spans only list trading days.
        var days = spanDays <= 45
            ? Enumerable.Range(0, spanDays + 1).Select(minDate.AddDays)
            : dailyMap.Keys.OrderBy(d => d);

        long cumulative = 0;
        foreach (var day in days)
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

        return points;
    }

    private static string OutcomeBadge(long netProfitCopper)
    {
        if (netProfitCopper > 0) return "Winner";
        if (netProfitCopper == 0) return "Break-even";
        return "Loss";
    }

    private static TValue GetOrAdd<TKey, TValue>(Dictionary<TKey, TValue> map, TKey key)
        where TKey : notnull
        where TValue : new()
    {
        if (!map.TryGetValue(key, out var value))
        {
            value = new TValue();
            map[key] = value;
        }

        return value;
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
