using Mmopal.Contracts;

namespace Mmopal.Gw2;

// The session request the desktop app sends to the worker (everything a session needs to run).
public sealed record TradingReplayRequest(
    IReadOnlyList<ItemCandidate>? Candidates = null,
    IReadOnlyList<string>? Blacklist = null,
    IReadOnlyList<Transaction>? Transactions = null,
    AccountBalance? Balance = null,
    DateTimeOffset? ObservedAt = null,
    ProfitabilityPolicy? Policy = null,
    string Source = "replay",
    string? FixturePath = null,
    string? RecordPath = null,
    string? TemplateRoot = null,
    bool CaptureFrames = false,
    string? CaptureDirectory = null,
    string? VisibleItemName = null,
    string? CandidatesPath = null,
    string? BlacklistPath = null,
    Gw2ClientSettings? ClientSettings = null,
    long? MinimumBalanceCopper = null,
    long? DefaultOrderQuantity = null,
    int OverbidBufferMinCopper = 1,
    int OverbidBufferMaxCopper = 5,
    int UndercutBufferMinCopper = 1,
    int UndercutBufferMaxCopper = 5,
    bool ArmLiveInput = false,
    bool EnableInventorySellPass = false,
    bool EnableOutbidBuyRefresh = true,
    bool UsePortfolioIntelligence = true,
    string? ActiveAccountName = null,
    SessionReportPolicySettings? ReportSettings = null,
    // Re-run the Market Scanner with these filters every AutoRescanIntervalMinutes during the session (0 = off).
    MarketScanFilter? AutoRescanFilter = null,
    int AutoRescanIntervalMinutes = 0,
    // Gold the player keeps out of trading (see GoldGoal).
    long KeepGoldCopper = 0)
{
    public IReadOnlyList<ItemCandidate> EffectiveCandidates => Candidates ?? Array.Empty<ItemCandidate>();
    public IReadOnlyList<string> EffectiveBlacklist => Blacklist ?? Array.Empty<string>();
    public IReadOnlyList<Transaction> EffectiveTransactions => Transactions ?? Array.Empty<Transaction>();

    public MarketSnapshot ToMarketSnapshot() => new(
        EffectiveTransactions,
        Balance,
        ObservedAt ?? DateTimeOffset.UtcNow,
        "replay");
}
