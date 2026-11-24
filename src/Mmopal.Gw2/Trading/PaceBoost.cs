namespace Mmopal.Gw2;

/// <summary>What one cycle left undone, for deciding the next one's pace.</summary>
/// <param name="Waiting">Items that only the per-cycle limit kept from being ordered.</param>
/// <param name="Accepted">Orders the cycle placed.</param>
/// <param name="CapReached">The share of gold allowed in buy orders stopped an order.</param>
/// <param name="SpendableCopper">Wallet above the reserve floor.</param>
/// <param name="BuyOrdersCopper">Gold in buy orders.</param>
/// <param name="TotalCopper">Wallet, buy orders and listings after fees.</param>
public sealed record CyclePressure(int Waiting, int Accepted, bool CapReached, long SpendableCopper, long BuyOrdersCopper, long TotalCopper)
{
    public double IdleShare => TotalCopper <= 0 ? 0 : (double)Math.Max(0, SpendableCopper) / TotalCopper;
}

/// <summary>
/// Uses the Fast numbers for as long as they are needed: more items wait than a cycle may order and much of the gold is
/// idle. On 24 Nov about 590g of 950g sat in the wallet from 02:00 to 07:00, the best buying hours, with 19-59 items
/// waiting behind three orders per cycle. Latency settings are untouched, so every click waits as long as before.
/// </summary>
public sealed class PaceBoost
{
    public const int BoostedPerPass = 5;
    public static readonly TimeSpan BoostedPause = TimeSpan.FromSeconds(25);
    private const double MinIdleShare = 0.25;
    private const int MinWaiting = 5;
    // Cycles in a row that had nothing to order although gold is idle.
    public const int IdleCyclesBeforeWiderScan = 5;
    public static readonly TimeSpan WiderScanInterval = TimeSpan.FromHours(1);

    private int _idleCycles;
    private DateTimeOffset _lastWiderScan = DateTimeOffset.MinValue;

    public bool Active { get; private set; }

    /// <summary>Updates from the last cycle; true when the boost was just switched on or off. Safe pace is never boosted.</summary>
    public bool Update(CyclePressure pressure, bool allowed)
    {
        _idleCycles = pressure is { Accepted: 0, Waiting: 0, CapReached: false } && pressure.IdleShare >= MinIdleShare ? _idleCycles + 1 : 0;
        // On needs a real backlog; off only once the backlog, the room under the cap or the idle gold is gone.
        var want = allowed && !pressure.CapReached && (Active
            ? pressure.Waiting > 0 && pressure.IdleShare >= MinIdleShare * 0.6
            : pressure.Waiting >= MinWaiting && pressure.IdleShare >= MinIdleShare);
        var changed = want != Active;
        Active = want;
        return changed;
    }

    public int PerPass(int profilePerPass) => Active ? Math.Max(profilePerPass, BoostedPerPass) : profilePerPass;

    public TimeSpan Pause(TimeSpan profilePause) => Active && profilePause > BoostedPause ? BoostedPause : profilePause;

    /// <summary>
    /// True once the list has run out with gold idle (75 cycles of 24 Nov ordered nothing, ~364g idle), at most hourly.
    /// </summary>
    public bool TakeWiderScan(DateTimeOffset nowUtc)
    {
        if (_idleCycles < IdleCyclesBeforeWiderScan || nowUtc - _lastWiderScan < WiderScanInterval)
        {
            return false;
        }

        _lastWiderScan = nowUtc;
        _idleCycles = 0;
        return true;
    }
}
