using Mmopal.Contracts;
using Mmopal.Gw2;

namespace Mmopal.Worker;

/// <summary>
/// Live Trading Post executor that drives real clicks/keystrokes strictly when RunMode.Live
/// is explicitly armed by the operator.
/// </summary>
public sealed class LiveTradingActionExecutor : ITradingActionExecutor
{
    private readonly Gw2TradingPostUiDriver _driver;
    private readonly int _overbidMinCopper;
    private readonly int _overbidMaxCopper;

    public LiveTradingActionExecutor(
        Gw2TradingPostUiDriver driver,
        int overbidMinCopper = 1,
        int overbidMaxCopper = 5)
    {
        _driver = driver ?? throw new ArgumentNullException(nameof(driver));
        _overbidMinCopper = Math.Max(0, overbidMinCopper);
        _overbidMaxCopper = Math.Max(_overbidMinCopper, overbidMaxCopper);
    }

    public Task<TradingActionResult> ExecuteAsync(TradingAction action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (action.Kind == TradingActionKind.PlaceBuyOrder)
        {
            return _driver.PlaceBuyOrderAsync(action, _overbidMinCopper, _overbidMaxCopper, cancellationToken);
        }

        return Task.FromResult(new TradingActionResult(
            action,
            TradingActionStatus.Skipped,
            $"Action kind '{action.Kind}' is handled by the inventory sell pass."));
    }
}
