using Mmopal.Contracts;

namespace Mmopal.Gw2;

// Results returned by the GW2 API client: live price quotes, key validation, portfolio sync.
public sealed record Gw2LivePriceQuote(
    int ItemId,
    long HighestBuyCopper,
    long BuyQuantity,
    long LowestSellCopper,
    long SellQuantity);

public sealed record Gw2ApiKeyValidationResult(
    bool IsValid,
    string? AccountName,
    IReadOnlyList<string> Permissions,
    long? CoinCopper,
    string Message);

public sealed record Gw2PortfolioSyncResult(
    int NewlyArchivedTransactions,
    int TotalArchivedTransactions,
    IReadOnlyList<Gw2ActiveOrder> ActiveOrders,
    Gw2DeliverySnapshot Delivery,
    Gw2EquitySnapshot LatestSnapshot,
    string AccountName = "");
