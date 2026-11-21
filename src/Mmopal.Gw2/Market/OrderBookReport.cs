using Mmopal.Contracts;

namespace Mmopal.Gw2;

public enum BookPosition
{
    Unknown,
    Behind,
    Matching,
    Ahead
}

/// <summary>
/// Compares current orders with the public book. A buy is behind when the highest bid is above
/// our price. A sell is behind when the lowest ask is below our price. This report does not place,
/// cancel, or reprice anything.
/// </summary>
public sealed record OrderBookReport(int Buys, int Sells, int BuysBehind, int SellsBehind, int Unknown)
{
    public static OrderBookReport Summarize(IEnumerable<Transaction>? transactions)
    {
        var buys = 0;
        var sells = 0;
        var buysBehind = 0;
        var sellsBehind = 0;
        var unknown = 0;
        foreach (var transaction in transactions ?? Array.Empty<Transaction>())
        {
            if (transaction.Type == TransactionType.Buy)
            {
                buys++;
                switch (Position(transaction))
                {
                    case BookPosition.Behind:
                        buysBehind++;
                        break;
                    case BookPosition.Unknown:
                        unknown++;
                        break;
                }
            }
            else
            {
                sells++;
                switch (Position(transaction))
                {
                    case BookPosition.Behind:
                        sellsBehind++;
                        break;
                    case BookPosition.Unknown:
                        unknown++;
                        break;
                }
            }
        }

        return new OrderBookReport(buys, sells, buysBehind, sellsBehind, unknown);
    }

    public static BookPosition Position(Transaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var book = transaction.Type == TransactionType.Buy
            ? transaction.MinimumBuyPriceCopper
            : transaction.MinimumSellPriceCopper;
        if (book is not long quote || quote <= 0)
        {
            return BookPosition.Unknown;
        }

        if (transaction.Type == TransactionType.Buy)
        {
            if (quote > transaction.PriceCopper) return BookPosition.Behind;
            if (quote < transaction.PriceCopper) return BookPosition.Ahead;
            return BookPosition.Matching;
        }

        if (quote < transaction.PriceCopper) return BookPosition.Behind;
        if (quote > transaction.PriceCopper) return BookPosition.Ahead;
        return BookPosition.Matching;
    }

    public string Describe()
    {
        if (Buys == 0 && Sells == 0)
        {
            return "No current orders.";
        }

        var line = SideCounts();
        var notes = new List<string>();
        if (BuysBehind > 0)
        {
            notes.Add($"{BuysBehind} {(BuysBehind == 1 ? "buy is" : "buys are")} behind the top bid");
        }

        if (SellsBehind > 0)
        {
            notes.Add($"{SellsBehind} {(SellsBehind == 1 ? "sell is" : "sells are")} behind the lowest ask");
        }

        if (notes.Count == 0 && Unknown == 0)
        {
            return line + " None are behind the book.";
        }

        if (notes.Count > 0)
        {
            line = line + " " + string.Join(". ", notes) + ".";
        }

        if (Unknown > 0)
        {
            line += $" Listing prices are missing for {Unknown} {(Unknown == 1 ? "order" : "orders")}.";
        }

        return line;
    }

    private string SideCounts()
    {
        var buys = Count(Buys, "buy", "buys");
        var sells = Count(Sells, "sell", "sells");
        if (Buys > 0 && Sells > 0) return $"{buys} and {sells}.";
        if (Buys > 0) return $"{buys}.";
        return $"{sells}.";
    }

    private static string Count(int value, string singular, string plural) =>
        $"{value} {(value == 1 ? singular : plural)}";
}
