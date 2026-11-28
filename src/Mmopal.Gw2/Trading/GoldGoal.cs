namespace Mmopal.Gw2;

/// <summary>
/// Gold the player keeps out of trading, per account: never touched. It stays in the wallet on top of the reserve, and
/// Auto-Tune sizes everything from the rest.
/// </summary>
public sealed record GoldGoal(long KeepCopper = 0)
{
    public bool IsSet => KeepCopper > 0;

    /// <summary>The wallet below which no buy order is placed.</summary>
    public long Floor(long reserveCopper) => Math.Max(0, reserveCopper) + Math.Max(0, KeepCopper);
}
