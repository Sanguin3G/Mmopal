namespace Mmopal.Contracts;

/// <summary>
/// One trading cycle in numbers: where its time went, what it decided and what the account looked like. Stored per
/// cycle, so a long run can be analysed (and compared with other runs) without reading its log.
/// </summary>
/// <param name="Seconds">Seconds per step (open and collect, sync, buy, sell, cancel, relist, swap, pause before it).</param>
/// <param name="SkipReasons">How many items were skipped, by reason.</param>
public sealed record CycleMetrics(
    DateTimeOffset At,
    string? Character,
    string MarketPhase,
    IReadOnlyDictionary<string, double> Seconds,
    int Evaluated,
    int Accepted,
    IReadOnlyDictionary<string, int> SkipReasons,
    long? WalletCopper,
    long BuyOrdersCopper,
    long OldBuyOrdersCopper,
    int OpenBuys,
    int OutbidBuys,
    long ListingsNetCopper,
    int OpenListings,
    int UndercutListings,
    int SellQueue,
    long UnsoldUnits,
    int WaitingOnStock);

/// <summary>One thing that happened to one item (or the box, or a character): what, how it ended and why.</summary>
/// <param name="Kind">buy, sell, cancel, relist, delivery, swap or recovery.</param>
/// <param name="Outcome">A short stable code ("placed", "held", "not-found", "title-unreadable"), so runs can be counted together.</param>
public sealed record SessionEvent(
    DateTimeOffset At,
    string Kind,
    string Outcome,
    string? ItemName = null,
    string? Character = null,
    long? UnitCopper = null,
    string? Detail = null);
