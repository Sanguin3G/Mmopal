using Mmopal.Contracts;
using Mmopal.Gw2;

namespace Mmopal.Domain.Tests;

public sealed class OrderBookReportTests
{
    private static readonly DateTimeOffset Created = new(2024, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [Fact]
    public void Book_position_counts_only_a_higher_bid_or_a_lower_ask_as_behind()
    {
        var report = OrderBookReport.Summarize(new[]
        {
            Order(TransactionType.Buy, 100, minimumBuy: 150),
            Order(TransactionType.Buy, 100, minimumBuy: 100),
            Order(TransactionType.Buy, 100, minimumBuy: 80),
            Order(TransactionType.Buy, 100, minimumBuy: null),
            Order(TransactionType.Buy, 100, minimumBuy: 0),
            Order(TransactionType.Sell, 200, minimumSell: 150),
            Order(TransactionType.Sell, 200, minimumSell: 200),
            Order(TransactionType.Sell, 200, minimumSell: 250),
            Order(TransactionType.Sell, 200, minimumSell: null)
        });

        Assert.Equal(5, report.Buys);
        Assert.Equal(4, report.Sells);
        Assert.Equal(1, report.BuysBehind);
        Assert.Equal(1, report.SellsBehind);
        Assert.Equal(3, report.Unknown);
        Assert.Equal(BookPosition.Behind, OrderBookReport.Position(Order(TransactionType.Buy, 100, minimumBuy: 150)));
        Assert.Equal(BookPosition.Matching, OrderBookReport.Position(Order(TransactionType.Buy, 100, minimumBuy: 100)));
        Assert.Equal(BookPosition.Ahead, OrderBookReport.Position(Order(TransactionType.Buy, 100, minimumBuy: 80)));
        Assert.Equal(BookPosition.Unknown, OrderBookReport.Position(Order(TransactionType.Buy, 100, minimumBuy: null)));
        Assert.Equal(BookPosition.Behind, OrderBookReport.Position(Order(TransactionType.Sell, 200, minimumSell: 150)));
        Assert.Equal(BookPosition.Matching, OrderBookReport.Position(Order(TransactionType.Sell, 200, minimumSell: 200)));
        Assert.Equal(BookPosition.Ahead, OrderBookReport.Position(Order(TransactionType.Sell, 200, minimumSell: 250)));
        Assert.Equal(BookPosition.Unknown, OrderBookReport.Position(Order(TransactionType.Sell, 200, minimumSell: null)));
    }

    [Fact]
    public void Empty_book_says_there_are_no_current_orders()
    {
        Assert.Equal("No current orders.", OrderBookReport.Summarize(null).Describe());
        Assert.Equal("No current orders.", OrderBookReport.Summarize(Array.Empty<Transaction>()).Describe());
    }

    [Fact]
    public void Describe_names_a_buy_behind_the_top_bid_and_a_sell_behind_the_lowest_ask()
    {
        var report = OrderBookReport.Summarize(new[]
        {
            Order(TransactionType.Buy, 100, minimumBuy: 150),
            Order(TransactionType.Sell, 200, minimumSell: 150)
        });

        Assert.Equal(
            "1 buy and 1 sell. 1 buy is behind the top bid. 1 sell is behind the lowest ask.",
            report.Describe());
    }

    [Fact]
    public void Describe_says_when_none_are_behind_and_when_a_quote_is_missing()
    {
        Assert.Equal(
            "1 buy. None are behind the book.",
            OrderBookReport.Summarize(new[] { Order(TransactionType.Buy, 100, minimumBuy: 100) }).Describe());
        Assert.Equal(
            "1 sell. Listing prices are missing for 1 order.",
            OrderBookReport.Summarize(new[] { Order(TransactionType.Sell, 200, minimumSell: null) }).Describe());
    }

    [Fact]
    public void An_overbid_buy_does_not_become_an_order_action()
    {
        var decisions = new CandidatePlanner().Evaluate(
            new[] { new ItemCandidate("Glob of Ectoplasm", BuyPriceCopper: 100, SellPriceCopper: 200) },
            new TransactionIndex(new[] { Order(TransactionType.Buy, 100, minimumBuy: 150, name: "Glob of Ectoplasm") }),
            new BlacklistMatcher(Array.Empty<string>()),
            new ProfitabilityPolicy());

        var decision = Assert.Single(decisions);
        Assert.Equal(CandidateDecisionKind.Skipped, decision.Kind);
        Assert.Null(decision.Action);
        Assert.DoesNotContain("cancel", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    private static Transaction Order(
        TransactionType type,
        long price,
        long? minimumSell = null,
        long? minimumBuy = null,
        string name = "Item") =>
        new(type, name, price, 1, Created, minimumSell, minimumBuy);
}
