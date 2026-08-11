using Mmopal.Contracts;
using Mmopal.Gw2;
using Mmopal.Infrastructure;

namespace Mmopal.Domain.Tests;

public sealed class TradingDomainTests
{
    [Fact]
    public void Currency_round_trip_preserves_copper()
    {
        var copper = CurrencyMath.ToCopper(12, 34, 56);
        Assert.Equal(123456, copper);
        Assert.Equal((12L, 34, 56), CurrencyMath.FromCopper(copper));
    }

    [Fact]
    public void Profitability_applies_both_fees_and_threshold()
    {
        var result = new ProfitabilityEngine().Evaluate(
            buyPriceCopper: 10_000,
            sellPriceCopper: 20_000,
            new ProfitabilityPolicy(MinimumProfitPercentage: 50));

        Assert.True(result.IsValid);
        Assert.True(result.IsProfitable);
        Assert.Equal(1_000, result.ListingFeeCopper);
        Assert.Equal(2_000, result.ExchangeFeeCopper);
        Assert.Equal(7_000, result.ProfitCopper);
        Assert.Equal(70m, result.ProfitPercentage);
    }

    [Fact]
    public void Invalid_price_is_a_safe_non_action()
    {
        var result = new ProfitabilityEngine().Evaluate(0, 10_000, new ProfitabilityPolicy());

        Assert.False(result.IsValid);
        Assert.False(result.IsProfitable);
    }

    [Fact]
    public void Transaction_index_uses_normalized_names()
    {
        var index = new TransactionIndex(new[]
        {
            new Transaction(TransactionType.Buy, "  Mystic   Coin ", 100, 1, DateTimeOffset.UtcNow, null, 90)
        });

        Assert.True(index.Contains("mystic coin", TransactionType.Buy));
    }

    [Fact]
    public void Wildcard_blacklist_does_not_use_lua_pattern_semantics()
    {
        var blacklist = new BlacklistMatcher(new[] { "Mystic *" });

        Assert.True(blacklist.IsBlocked("Mystic Coin"));
        Assert.False(blacklist.IsBlocked("Ancient Coin"));
    }

    [Fact]
    public void Delimited_parser_preserves_empty_and_quoted_values()
    {
        const string csv = "Name;Quantity;Note\n\"Mystic;Coin\";;\"hello \"\"world\"\"\"";
        var rows = DelimitedTextParser.Parse(csv);

        Assert.Single(rows);
        Assert.Equal("Mystic;Coin", rows[0]["Name"]);
        Assert.Equal(string.Empty, rows[0]["Quantity"]);
        Assert.Equal("hello \"world\"", rows[0]["Note"]);
    }
}
