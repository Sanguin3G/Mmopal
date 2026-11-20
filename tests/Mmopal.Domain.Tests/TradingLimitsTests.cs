using Mmopal.Gw2;

namespace Mmopal.Domain.Tests;

public sealed class TradingLimitsTests
{
    [Fact]
    public void Limits_convert_gold_to_copper_and_leave_a_blank_cap_open()
    {
        Assert.True(TradingLimits.TryParse("12.5", "1.5", out var limits, out var error));
        Assert.Null(error);
        var policy = limits.ToPolicy();

        Assert.Equal(12.5m, policy.MinimumProfitPercentage);
        Assert.Equal(15_000, policy.MaximumBuyPriceCopper);
        Assert.Equal(500, policy.ListingFeeBasisPoints);

        Assert.True(TradingLimits.TryParse("12,5", "1,5", out var comma, out _));
        Assert.Equal(12.5m, comma.MinimumProfitPercentage);
        Assert.Equal(15_000, comma.ToPolicy().MaximumBuyPriceCopper);

        Assert.True(TradingLimits.TryParse(" ", " ", out var open, out _));
        Assert.Equal(0, open.MinimumProfitPercentage);
        Assert.Null(open.ToPolicy().MaximumBuyPriceCopper);
    }

    [Fact]
    public void Zero_gold_is_a_cap_of_zero_copper()
    {
        Assert.True(TradingLimits.TryParse("0", "0", out var limits, out var error));

        Assert.Null(error);
        Assert.Equal(0, limits.ToPolicy().MaximumBuyPriceCopper);
    }

    [Fact]
    public void Negative_profit_percent_is_rejected()
    {
        Assert.False(TradingLimits.TryParse("-1", "", out _, out var error));

        Assert.Contains("profit", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Thousands_separators_are_parsed_as_thousands_without_breaking_decimals()
    {
        Assert.True(TradingLimits.TryParse("10", "1,000", out var thousand, out var error));
        Assert.Null(error);
        Assert.Equal(1_000m, thousand.MaximumBuyPriceGold);
        Assert.Equal(10_000_000L, thousand.ToPolicy().MaximumBuyPriceCopper);

        Assert.True(TradingLimits.TryParse("10", "1,000,000", out var million, out _));
        Assert.Equal(1_000_000m, million.MaximumBuyPriceGold);

        Assert.True(TradingLimits.TryParse("10", "1,234.5", out var usMixed, out _));
        Assert.Equal(1_234.5m, usMixed.MaximumBuyPriceGold);

        Assert.True(TradingLimits.TryParse("10", "1.234,5", out var euMixed, out _));
        Assert.Equal(1_234.5m, euMixed.MaximumBuyPriceGold);

        Assert.True(TradingLimits.TryParse("10", "1.500", out var threeDecimalDot, out _));
        Assert.Equal(1.5m, threeDecimalDot.MaximumBuyPriceGold);

        Assert.False(TradingLimits.TryParse("10", "1,23,45", out _, out _));
    }

    [Fact]
    public void Limits_file_round_trips_and_a_missing_file_uses_defaults()
    {
        var path = Path.Combine(Path.GetTempPath(), "mmopal-limits-" + Guid.NewGuid().ToString("N") + ".json");
        var missing = path + ".missing";
        try
        {
            TradingLimitsFile.Save(path, new TradingLimits(10, 2));
            var loaded = TradingLimitsFile.LoadOrDefault(path);
            var absent = TradingLimitsFile.LoadOrDefault(missing);

            Assert.Equal(10, loaded.MinimumProfitPercentage);
            Assert.Equal(2, loaded.MaximumBuyPriceGold);
            Assert.Equal(0, absent.MinimumProfitPercentage);
            Assert.Null(absent.MaximumBuyPriceGold);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
