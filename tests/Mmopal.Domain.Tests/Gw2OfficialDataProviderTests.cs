using System.Net;
using System.Text;
using Mmopal.Contracts;
using Mmopal.Gw2;

namespace Mmopal.Domain.Tests;

public sealed class Gw2OfficialDataProviderTests
{
    [Fact]
    public async Task Official_api_reads_transactions_and_coin_without_putting_the_key_in_the_url()
    {
        var handler = new ScriptedHandler(request =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-key", request.Headers.Authorization?.Parameter);
            Assert.DoesNotContain("test-key", request.RequestUri?.ToString(), StringComparison.Ordinal);
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path == "/v2/commerce/prices")
            {
                Assert.Contains("ids=19721", request.RequestUri?.Query, StringComparison.Ordinal);
            }

            var body = path switch
            {
                "/v2/commerce/transactions/current/buys" =>
                    """[{"item_id":19721,"price":100,"quantity":2,"created":"2024-01-02T03:04:05Z"}]""",
                "/v2/commerce/transactions/current/sells" => "[]",
                "/v2/items" => """[{"id":19721,"name":"Glob of Ectoplasm"}]""",
                "/v2/commerce/prices" =>
                    """[{"id":19721,"whitelisted":false,"buys":{"quantity":10,"unit_price":80},"sells":{"quantity":4,"unit_price":120}}]""",
                "/v2/account/wallet" => """[{"id":1,"value":12345},{"id":2,"value":9}]""",
                _ => throw new InvalidOperationException(path)
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.guildwars2.com/") };
        var provider = new Gw2OfficialDataProvider(client, "test-key");

        var transactions = await provider.GetTransactionsAsync();
        var balance = await provider.GetBalanceAsync();

        var transaction = Assert.Single(transactions);
        Assert.Equal(TransactionType.Buy, transaction.Type);
        Assert.Equal("Glob of Ectoplasm", transaction.ItemName);
        Assert.Equal(100, transaction.PriceCopper);
        Assert.Equal(2, transaction.Quantity);
        Assert.Equal(120, transaction.MinimumSellPriceCopper);
        Assert.Equal(80, transaction.MinimumBuyPriceCopper);
        Assert.NotNull(balance);
        Assert.Equal(12345, balance.Copper);
    }

    [Fact]
    public async Task Official_api_leaves_listing_prices_empty_when_a_side_is_missing_or_not_positive()
    {
        var handler = new ScriptedHandler(request =>
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var body = path switch
            {
                "/v2/commerce/transactions/current/buys" =>
                    """[{"item_id":19721,"price":100,"quantity":2,"created":"2024-01-02T03:04:05Z"}]""",
                "/v2/commerce/transactions/current/sells" =>
                    """[{"item_id":19722,"price":50,"quantity":1,"created":"2024-01-02T03:04:05Z"}]""",
                "/v2/items" => """[{"id":19721,"name":"Glob of Ectoplasm"},{"id":19722,"name":"Lump of Coal"}]""",
                "/v2/commerce/prices" =>
                    """[{"id":19721,"buys":{"quantity":0,"unit_price":0}},{"id":19722,"sells":{"quantity":3,"unit_price":40}}]""",
                _ => throw new InvalidOperationException(path)
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.guildwars2.com/") };
        var provider = new Gw2OfficialDataProvider(client, "test-key");

        var transactions = await provider.GetTransactionsAsync();

        var buy = Assert.Single(transactions, row => row.Type == TransactionType.Buy);
        Assert.Null(buy.MinimumSellPriceCopper);
        Assert.Null(buy.MinimumBuyPriceCopper);
        var sell = Assert.Single(transactions, row => row.Type == TransactionType.Sell);
        Assert.Equal(40, sell.MinimumSellPriceCopper);
        Assert.Null(sell.MinimumBuyPriceCopper);
    }

    [Fact]
    public async Task Official_api_rejects_a_blank_key_before_sending()
    {
        var handler = new ScriptedHandler(_ => throw new InvalidOperationException("The request should not be sent."));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.guildwars2.com/") };
        var provider = new Gw2OfficialDataProvider(client, " ");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetTransactionsAsync());

        Assert.Contains("MMOPAL_Native__Gw2ApiKey", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Official_api_reports_a_rejected_key_without_repeating_it()
    {
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.guildwars2.com/") };
        var provider = new Gw2OfficialDataProvider(client, "super-secret-key");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetBalanceAsync());

        Assert.DoesNotContain("super-secret-key", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Official_api_paginates_all_open_buy_and_sell_orders()
    {
        var requestedPages = new List<string>();
        var handler = new ScriptedHandler(request =>
        {
            var pathAndQuery = request.RequestUri?.PathAndQuery ?? string.Empty;
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            requestedPages.Add(pathAndQuery);

            HttpResponseMessage Ok(string json, int? pageTotal = null)
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
                if (pageTotal.HasValue)
                {
                    response.Headers.Add("X-Page-Total", pageTotal.Value.ToString());
                }

                return response;
            }

            return pathAndQuery switch
            {
                "/v2/commerce/transactions/current/buys?page=0&page_size=200" =>
                    Ok("""[{"item_id":19721,"price":100,"quantity":1,"created":"2024-01-02T03:04:05Z"}]""", pageTotal: 2),
                "/v2/commerce/transactions/current/buys?page=1&page_size=200" =>
                    Ok("""[{"item_id":19722,"price":110,"quantity":2,"created":"2024-01-02T03:04:06Z"}]""", pageTotal: 2),
                "/v2/commerce/transactions/current/sells?page=0&page_size=200" =>
                    Ok("""[{"item_id":19723,"price":200,"quantity":3,"created":"2024-01-02T03:04:07Z"}]""", pageTotal: 2),
                "/v2/commerce/transactions/current/sells?page=1&page_size=200" =>
                    Ok("""[{"item_id":19724,"price":210,"quantity":4,"created":"2024-01-02T03:04:08Z"}]""", pageTotal: 2),
                _ when path == "/v2/items" =>
                    Ok("""[{"id":19721,"name":"Item A"},{"id":19722,"name":"Item B"},{"id":19723,"name":"Item C"},{"id":19724,"name":"Item D"}]"""),
                _ when path == "/v2/commerce/prices" =>
                    Ok("""[{"id":19721,"buys":{"quantity":1,"unit_price":100},"sells":{"quantity":1,"unit_price":120}}]"""),
                _ => throw new InvalidOperationException(pathAndQuery)
            };
        });

        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.guildwars2.com/") };
        var provider = new Gw2OfficialDataProvider(client, "test-key");

        var transactions = await provider.GetTransactionsAsync();

        Assert.Equal(4, transactions.Count);
        Assert.Equal(2, transactions.Count(t => t.Type == TransactionType.Buy));
        Assert.Equal(2, transactions.Count(t => t.Type == TransactionType.Sell));
        Assert.Contains("/v2/commerce/transactions/current/buys?page=1&page_size=200", requestedPages);
        Assert.Contains("/v2/commerce/transactions/current/sells?page=1&page_size=200", requestedPages);
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_respond(request));
    }
}
