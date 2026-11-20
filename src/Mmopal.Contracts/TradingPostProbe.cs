namespace Mmopal.Contracts;

public sealed record TradingPostUiProbeOptions(
    string TemplateRoot,
    CaptureRegion? CaptureRegion = null,
    int MaxWaitMilliseconds = 100,
    decimal MatchScore = 0.8m,
    bool ReadVisiblePrice = true,
    string? VisibleItemName = null)
{
    public CaptureRegion EffectiveCaptureRegion => CaptureRegion ?? Mmopal.Contracts.CaptureRegion.FullScreen1024x768;
}

public interface ITradingPostUiProbe
{
    Task<TradingPostUiSnapshot> ProbeAsync(
        TradingPostUiProbeOptions options,
        CancellationToken cancellationToken = default);

    Task<VisiblePriceObservation?> ReadVisiblePriceAsync(
        TradingPostUiProbeOptions options,
        CancellationToken cancellationToken = default);
}
