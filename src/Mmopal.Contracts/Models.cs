namespace Mmopal.Contracts;

public enum BotKind
{
    TradingPost,
    Fishing
}

public enum RunMode
{
    DryRun,
    Observe,
    Live
}

public enum WorkerState
{
    Idle,
    Starting,
    LaunchingClient,
    WaitingForLogin,
    TradingPostReady,
    ProcessingTrading,
    Fishing,
    Paused,
    Recovering,
    Stopping,
    Stopped,
    Faulted,
    NeedsAttention
}

public enum TransactionType
{
    Buy,
    Sell
}

public enum PriceSource
{
    Api,
    Ocr,
    Replay,
    Unknown
}

public enum TradingPostObservationStatus
{
    Ready,
    Partial,
    NotReady,
    Unavailable,
    Failed
}

public enum LicenseFeature
{
    CustomProfiles,
    AdvancedDiagnostics,
    ExperimentalAdapters,
    Branding
}

public sealed record ItemCandidate(
    string ItemName,
    string? Rarity = null,
    long? Quantity = null,
    decimal? MinimumProfitPercentage = null,
    long? MaximumPriceCopper = null,
    decimal? Roi = null,
    long? SellPriceCopper = null,
    long? BuyPriceCopper = null,
    int? ItemId = null);

public sealed record Transaction(
    TransactionType Type,
    string ItemName,
    long PriceCopper,
    long Quantity,
    DateTimeOffset Created,
    long? MinimumSellPriceCopper,
    long? MinimumBuyPriceCopper);

public sealed record AccountBalance(long Copper)
{
    public decimal Gold => Copper / 10_000m;
}

public sealed record PriceQuote(
    long Copper,
    PriceSource Source,
    DateTimeOffset ObservedAt,
    decimal Confidence = 1m);

public sealed record MarketSnapshot(
    IReadOnlyList<Transaction> Transactions,
    AccountBalance? Balance,
    DateTimeOffset ObservedAt,
    string Source);

public sealed record TradingPostUiSnapshot(
    bool TradingPostOpen,
    bool ItemWindowOpen,
    IReadOnlyDictionary<string, bool> DetectedTemplates,
    DateTimeOffset ObservedAt);

public sealed record VisiblePriceObservation(
    string? ItemName,
    PriceQuote? Price,
    string? Error = null);

public sealed record TradingPostObservation(
    string Source,
    DateTimeOffset ObservedAt,
    MarketSnapshot Market,
    TradingPostObservationStatus Status,
    TradingPostUiSnapshot Ui,
    VisiblePriceObservation? VisiblePrice,
    IReadOnlyDictionary<string, long> TimingsMilliseconds,
    IReadOnlyDictionary<string, string> Diagnostics,
    string? Error = null);

public sealed record ProfitabilityResult(
    bool IsValid,
    bool IsProfitable,
    long BuyPriceCopper,
    long SellPriceCopper,
    long ListingFeeCopper,
    long ExchangeFeeCopper,
    long NetSaleCopper,
    long ProfitCopper,
    decimal ProfitPercentage,
    string? Reason);

public sealed record LicenseSnapshot(
    bool IsEnabled,
    bool IsValid,
    IReadOnlySet<LicenseFeature> Features,
    string Provider,
    string? LicenseId,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset CheckedAt,
    string? Message)
{
    public bool Has(LicenseFeature feature) => Features.Contains(feature);
}

public sealed record WorkerStatus(
    WorkerState State,
    BotKind? Bot,
    RunMode? Mode,
    string? Profile,
    string? LastAction,
    DateTimeOffset Timestamp,
    string? Message = null);
