using Mmopal.Contracts;

namespace Mmopal.Gw2;

/// <summary>
/// Gold the player wants for playing. <paramref name="KeepCopper"/> is never touched: it is kept in the wallet on top of the
/// reserve, and Auto-Tune sizes everything from the rest. <paramref name="FreeCopper"/> by <paramref name="FreeByUtc"/> asks
/// for that much in the wallet at that time: no new buy orders take the wallet below it, Mmopal's own buy orders are
/// cancelled (which costs nothing) as far as needed, and listings sell as usual, never below what was paid.
/// </summary>
public sealed record GoldGoal(long KeepCopper = 0, long FreeCopper = 0, DateTimeOffset? FreeByUtc = null)
{
    public bool IsSet => KeepCopper > 0 || FreeCopper > 0;

    /// <summary>The wallet below which no buy order is placed.</summary>
    public long Floor(long reserveCopper) => Math.Max(Math.Max(0, reserveCopper) + Math.Max(0, KeepCopper), Math.Max(0, FreeCopper));

    /// <summary>Gold still missing from the wallet for the free-gold goal.</summary>
    public long Shortfall(long walletCopper) => Math.Max(0, FreeCopper - walletCopper);

    /// <summary>
    /// Mmopal's buy orders to cancel to free the missing gold: outbid ones first, then the oldest, until enough is freed.
    /// </summary>
    public IReadOnlyList<Transaction> OrdersToCancel(IEnumerable<Transaction> orders, long walletCopper, Func<Transaction, bool> isMmopals)
    {
        var missing = Shortfall(walletCopper);
        var picks = new List<Transaction>();
        foreach (var order in orders
                     .Where(order => order.Type == TransactionType.Buy && isMmopals(order))
                     .OrderByDescending(order => order.MinimumBuyPriceCopper is { } top && top > order.PriceCopper)
                     .ThenBy(order => order.Created))
        {
            if (missing <= 0) break;
            picks.Add(order);
            missing -= Math.Max(0, order.PriceCopper) * Math.Max(1, order.Quantity);
        }

        return picks;
    }

    /// <summary>Where the free-gold goal stands, for the status line and the log; null when there is none.</summary>
    public string? Describe(long walletCopper, long mmopalBuyOrdersCopper, long listingsNetCopper, DateTimeOffset nowUtc)
    {
        if (FreeCopper <= 0)
        {
            return null;
        }

        var by = FreeByUtc is { } at ? $" by {at.ToLocalTime():HH:mm}" : string.Empty;
        if (walletCopper >= FreeCopper)
        {
            return $"Gold for you: {Coins(walletCopper)} free of the {Coins(FreeCopper)} asked{by}; no buy order takes it below that.";
        }

        var reachable = walletCopper + mmopalBuyOrdersCopper + listingsNetCopper;
        var fromSales = Math.Max(0, FreeCopper - walletCopper - mmopalBuyOrdersCopper);
        return reachable < FreeCopper
            ? $"Gold for you: {Coins(walletCopper)} of {Coins(FreeCopper)}{by}. Even with every order cancelled and every listing sold it reaches {Coins(reachable)}; nothing is sold below what was paid."
            : $"Gold for you: {Coins(walletCopper)} of {Coins(FreeCopper)}{by}; cancelling buy orders" + (fromSales > 0 ? $", {Coins(fromSales)} more from sales." : ".");
    }

    private static string Coins(long copper) => MarketScanItem.FormatCoins(copper);
}
