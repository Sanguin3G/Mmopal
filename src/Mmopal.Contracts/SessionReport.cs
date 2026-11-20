namespace Mmopal.Contracts;

public sealed record SessionTradeEvent(
    DateTimeOffset Timestamp,
    string ActionType,
    string ItemName,
    long Quantity,
    long UnitPriceCopper,
    string Status,
    string Details = "");

public sealed record SessionIncidentEvent(
    DateTimeOffset Timestamp,
    string Severity,
    string Category,
    string Message,
    bool Recovered = false);

public sealed record TradingSessionReport(
    string Id,
    string AccountName,
    string BotKind,
    string RunMode,
    string DataSource,
    string Profile,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    double DurationSeconds,
    string Outcome,
    string OutcomeSummary,
    int CyclesExecuted,
    int DecisionsEvaluated,
    int BuyOrdersPlaced,
    int SellOrdersListed,
    int OutbidBuysRefreshed,
    int DeliveryCollections,
    long DeliveryCoinsCollectedCopper,
    int CharacterSwapsCompleted,
    int CharacterSwapsFailed,
    int RecoveryAttempts,
    int GameRestarts,
    int WarningCount,
    int ErrorCount,
    long? StartingWalletCopper,
    long? EndingWalletCopper,
    long EstimatedBuyVolumeCopper,
    int StartingOpenOrders,
    int EndingOpenOrders,
    IReadOnlyList<SessionTradeEvent> Trades,
    IReadOnlyList<SessionIncidentEvent> Incidents)
{
    public int TotalTradeActions => BuyOrdersPlaced + SellOrdersListed + OutbidBuysRefreshed;

    public bool HasIssues => ErrorCount > 0 || WarningCount > 0 || GameRestarts > 0 || CharacterSwapsFailed > 0;
}

public sealed record SessionReportPolicySettings(
    bool SaveSessionReports = true,
    bool IncludeDryRunAndObserve = false,
    int MinDurationMinutes = 5,
    bool AlwaysSaveIfTradesOccurred = true,
    bool AlwaysSaveOnErrors = true,
    int MaxSavedReports = 100);

public sealed record SessionReportNotification(
    TradingSessionReport Report,
    bool Saved,
    bool CapReached,
    bool AutoCloseApp,
    bool ShutdownPc,
    int ShutdownCountdownSeconds);
