using Mmopal.Gw2;

namespace Mmopal.Domain.Tests;

public sealed class StateMachineTests
{
    [Fact]
    public void Trading_machine_moves_through_explicit_states()
    {
        var machine = new TradingStateMachine();
        machine.Start();

        Assert.Equal(TradingState.EnsureClient, machine.State);
        machine.Advance(true, false, false, false);
        Assert.Equal(TradingState.EnsureTradingPost, machine.State);
        machine.Advance(true, true, false, false);
        Assert.Equal(TradingState.RefreshMarket, machine.State);
        machine.Advance(true, true, true, false);
        Assert.Equal(TradingState.ReconcileOrders, machine.State);
    }

    [Fact]
    public void Fishing_machine_can_pause_and_resume_without_recursion()
    {
        var machine = new FishingStateMachine();
        machine.Start();
        machine.Pause();
        Assert.Equal(FishingState.Paused, machine.State);
        machine.Resume();
        Assert.Equal(FishingState.PrepareGear, machine.State);
    }
}
