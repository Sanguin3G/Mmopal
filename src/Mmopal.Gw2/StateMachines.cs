using Mmopal.Contracts;

namespace Mmopal.Gw2;

public enum TradingState
{
    Idle,
    EnsureClient,
    EnsureTradingPost,
    RefreshMarket,
    ReconcileOrders,
    ProcessCandidates,
    ProcessSales,
    Checkpoint,
    Waiting,
    Paused,
    Recovering,
    Stopped,
    Faulted
}

public enum FishingState
{
    Idle,
    PrepareGear,
    CastLine,
    WaitForBite,
    HookFish,
    CatchingFish,
    Recovering,
    Paused,
    Stopped,
    Faulted
}

public sealed class TradingStateMachine
{
    public TradingState State { get; private set; } = TradingState.Idle;

    public void Start() => State = TradingState.EnsureClient;

    public void Pause()
    {
        if (State is not (TradingState.Stopped or TradingState.Faulted)) State = TradingState.Paused;
    }

    public void Resume()
    {
        if (State == TradingState.Paused) State = TradingState.RefreshMarket;
    }

    public void Stop() => State = TradingState.Stopped;

    public void TransitionTo(TradingState state)
    {
        if (state == TradingState.Idle) throw new ArgumentException("A running trading machine cannot transition back to Idle.", nameof(state));
        State = state;
    }

    public TradingState Advance(bool clientReady, bool tradingPostReady, bool marketAvailable, bool workAvailable)
    {
        State = State switch
        {
            TradingState.EnsureClient when !clientReady => TradingState.EnsureClient,
            TradingState.EnsureClient => TradingState.EnsureTradingPost,
            TradingState.EnsureTradingPost when !tradingPostReady => TradingState.EnsureTradingPost,
            TradingState.EnsureTradingPost => TradingState.RefreshMarket,
            TradingState.RefreshMarket when !marketAvailable => TradingState.Recovering,
            TradingState.RefreshMarket => TradingState.ReconcileOrders,
            TradingState.ReconcileOrders => TradingState.ProcessCandidates,
            TradingState.ProcessCandidates when workAvailable => TradingState.ProcessCandidates,
            TradingState.ProcessCandidates => TradingState.ProcessSales,
            TradingState.ProcessSales => TradingState.Checkpoint,
            TradingState.Checkpoint => TradingState.Waiting,
            TradingState.Waiting => TradingState.RefreshMarket,
            TradingState.Recovering => TradingState.EnsureClient,
            _ => State
        };

        return State;
    }
}

public sealed class FishingStateMachine
{
    public FishingState State { get; private set; } = FishingState.Idle;

    public void Start() => State = FishingState.PrepareGear;

    public void Pause()
    {
        if (State is not (FishingState.Stopped or FishingState.Faulted)) State = FishingState.Paused;
    }

    public void Resume()
    {
        if (State == FishingState.Paused) State = FishingState.PrepareGear;
    }

    public void Stop() => State = FishingState.Stopped;

    public FishingState Advance(bool gearReady, bool castReady, bool biteDetected, bool fishCaught)
    {
        State = State switch
        {
            FishingState.PrepareGear when !gearReady => FishingState.PrepareGear,
            FishingState.PrepareGear => FishingState.CastLine,
            FishingState.CastLine when !castReady => FishingState.CastLine,
            FishingState.CastLine => FishingState.WaitForBite,
            FishingState.WaitForBite when !biteDetected => FishingState.WaitForBite,
            FishingState.WaitForBite => FishingState.HookFish,
            FishingState.HookFish => FishingState.CatchingFish,
            FishingState.CatchingFish when !fishCaught => FishingState.CatchingFish,
            FishingState.CatchingFish => FishingState.PrepareGear,
            FishingState.Recovering => FishingState.PrepareGear,
            _ => State
        };

        return State;
    }
}
