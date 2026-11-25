namespace Mmopal.Gw2;

// Wallet-sized strategy presets (Starter, Balanced, High Capital, High-Volume Safe); SmartAutoTuner builds on these.
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
            Name: "Balanced (100g–3,000g)",
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
            Name: "High Capital (3,000g+)",
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

    /// <summary>
    /// Wallet kept back for listing fees. At 15% it held gold back on the nights when listings had filled up (24-25 Nov);
    /// listing one order costs 5% of it, and sales come in all night.
    /// </summary>
    private const decimal ReserveShare = 0.08m;

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
                Rationale = $"Total gold ~{gold:0.#}g (Starter tier): Max Buy {maxBuy:0.#}g (~5% cap/item) and {floor:0.#}g reserve for listing fees."
            };
        }

        // High Capital drops everything under 1g and under 50s profit and orders one unit at a time. At 1,158g (25 Nov) it
        // would have kept 205g of the 421g earned so far, from 273 of 1,327 units.
        if (gold < 3_000m)
        {
            var basePreset = All[1];
            var maxBuy = Math.Clamp(Math.Round(gold * 0.04m, 0), 5m, 120m);
            var floor = Math.Round(gold * ReserveShare, 0);
            return basePreset with
            {
                MaxBuyGold = maxBuy,
                RecommendedGoldFloor = floor,
                Rationale = $"Total gold ~{gold:0}g (Balanced tier): Max Buy {maxBuy:0}g (~4% cap/item) and {floor:0}g reserve for listing fees."
            };
        }

        var highPreset = All[2];
        var highMaxBuy = Math.Clamp(Math.Round(gold * 0.04m, 0), 120m, 150m);
        var highFloor = Math.Round(gold * ReserveShare, 0);
        return highPreset with
        {
            MaxBuyGold = highMaxBuy,
            RecommendedGoldFloor = highFloor,
            Rationale = $"Total gold ~{gold:0}g (High-Capital tier): Max Buy {highMaxBuy:0}g (~4% cap/item) and {highFloor:0}g reserve."
        };
    }
}
