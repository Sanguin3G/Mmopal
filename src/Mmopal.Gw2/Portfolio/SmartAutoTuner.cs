using System.Globalization;

namespace Mmopal.Gw2;

/// <summary>What Auto-Tune looks at: where the gold is now, and how the last two weeks of flips went.</summary>
public sealed record AutoTuneInputs(
    long WalletCopper,
    long ActiveBuysCopper = 0,
    long ActiveSellsNetCopper = 0,
    // FIFO results of the last <see cref="SmartAutoTuner.ResultsWindowDays"/> days; null when nothing is synced.
    TradingIncomeReport? RecentResults = null);

public sealed record AutoTuneResult(
    TradingStrategyPreset Preset,
    double MinTradesPerDay,
    IReadOnlyList<string> Adjustments,
    string Summary);

/// <summary>
/// Auto-Tune for operators who do not tune by hand. Step 1 sizes limits from total capital (wallet + gold in buy
/// orders + listings after fees), not the wallet alone. Step 2 nudges the settings from the operator's own recent
/// results: thin margins raise Min ROI, slow sales demand busier items, gold stuck in listings slows buying, idle
/// gold with profitable flips raises order size. Each step is small and bounded, so one bad day cannot swing it far.
/// Runs at launch and at every session start; never mid-session (wallet size barely changes within one session, and
/// shifting limits under open orders would make the bot inconsistent).
/// </summary>
public static class SmartAutoTuner
{
    public const int ResultsWindowDays = 14;
    public const int MinFlipsToLearn = 10;
    public const double BaseMinTradesPerDay = 10;

    public static AutoTuneResult Tune(AutoTuneInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var wallet = Math.Max(0, inputs.WalletCopper);
        var inListings = Math.Max(0, inputs.ActiveSellsNetCopper);
        var inOrders = Math.Max(0, inputs.ActiveBuysCopper) + inListings;
        var capital = wallet + inOrders;
        var preset = TradingStrategyPresets.RecommendForCapital(capital);
        var minTrades = BaseMinTradesPerDay;
        var notes = new List<string>();

        if (inOrders > 0)
        {
            notes.Add($"Sized for {Gold(capital)} in total ({Gold(wallet)} wallet + {Gold(inOrders)} in orders), not the wallet alone.");
        }

        var recent = inputs.RecentResults;
        var learnable = recent is not null && recent.MatchedUnitsSold >= MinFlipsToLearn ? recent : null;

        // Where the gold sits.
        if (capital > 0)
        {
            var sellShare = (double)inListings / capital;
            var walletShare = (double)wallet / capital;
            if (sellShare >= 0.5)
            {
                preset = preset with { MaxBuyGold = Math.Max(1m, Math.Round(preset.MaxBuyGold * 0.75m, 1)) };
                minTrades *= 1.5;
                notes.Add($"{Percent(sellShare)} of your gold is in unsold listings: buying cheaper, faster-selling items (Max Buy {preset.MaxBuyGold:0.#}g) until they sell.");
            }
            else if (walletShare >= 0.7 && learnable is not null && learnable.MatchedProfitCopper > 0)
            {
                var quantity = Math.Min(20, (long)Math.Ceiling(preset.DefaultOrderQuantity * 1.5));
                if (quantity > preset.DefaultOrderQuantity)
                {
                    preset = preset with { DefaultOrderQuantity = quantity };
                    notes.Add($"{Percent(walletShare)} of your gold sits idle and recent flips are profitable: order size raised to {quantity}.");
                }
            }
        }

        // What recent flips say.
        if (learnable is not null)
        {
            var roi = learnable.MatchedRoiPercent;
            var minRoi = preset.MinRoiPercent;
            if (roi < 5m)
            {
                minRoi += 3m;
                notes.Add($"Recent flips earned only {roi:0.#}% after fees: asking for a wider margin.");
            }
            else if (roi >= 15m && learnable.WinRatePercent >= 70)
            {
                minRoi -= 2m;
                notes.Add($"Recent flips are strong ({roi:0.#}% ROI, {learnable.WinRatePercent:0}% winners): Min ROI eased for more volume.");
            }

            if (learnable.WinRatePercent < 50)
            {
                minRoi += 2m;
                minTrades *= 1.5;
                notes.Add($"Only {learnable.WinRatePercent:0}% of recent flips made money: favouring busier items with a bigger cushion.");
            }

            var holdHours = AverageHoldHours(learnable);
            if (holdHours > 48)
            {
                minTrades *= 2;
                preset = preset with { MaxRoiPercent = Math.Min(preset.MaxRoiPercent, 60m) };
                notes.Add($"Items took {holdHours / 24:0.#} days to sell on average: requiring busier items and capping ROI at 60% (very wide spreads rarely fill).");
            }

            minRoi = Math.Clamp(minRoi, 8m, 25m);
            preset = preset with
            {
                MinRoiPercent = minRoi,
                MaxRoiPercent = Math.Max(preset.MaxRoiPercent, minRoi + 20m)
            };
        }
        else
        {
            notes.Add($"Not enough recent flips to learn from yet ({recent?.MatchedUnitsSold ?? 0} of {MinFlipsToLearn} sold in {ResultsWindowDays} days); using wallet-size defaults.");
        }

        minTrades = Math.Clamp(Math.Round(minTrades), 5, 50);
        var summary = $"{preset.Rationale} Min ROI {preset.MinRoiPercent:0}%, {minTrades:0}+ trades/day, order size {preset.DefaultOrderQuantity}. " +
                      string.Join(" ", notes);
        return new AutoTuneResult(preset, minTrades, notes, summary.Trim());
    }

    /// <summary>Hold time weighted by units, so one slow item among many fast ones does not dominate.</summary>
    public static double AverageHoldHours(TradingIncomeReport report)
    {
        var rows = report.ItemLeaderboard.Where(r => r.UnitsFlipped > 0).ToArray();
        var units = rows.Sum(r => (double)r.UnitsFlipped);
        return units <= 0 ? 0 : rows.Sum(r => r.AvgHoldHours * r.UnitsFlipped) / units;
    }

    private static string Gold(long copper) => string.Create(CultureInfo.InvariantCulture, $"{copper / 10_000m:0.#}g");

    private static string Percent(double share) => string.Create(CultureInfo.InvariantCulture, $"{share * 100:0}%");
}
