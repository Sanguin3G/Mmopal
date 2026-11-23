using System.Globalization;
using System.Text;
using Mmopal.Contracts;

namespace Mmopal.Gw2;

public static class SessionReportPolicy
{
    public static bool ShouldSave(TradingSessionReport report, SessionReportPolicySettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        var effective = settings ?? new SessionReportPolicySettings();
        if (!effective.SaveSessionReports)
        {
            return false;
        }

        var isLive = string.Equals(report.RunMode, nameof(RunMode.Live), StringComparison.OrdinalIgnoreCase);
        if (!isLive && !effective.IncludeDryRunAndObserve)
        {
            return false;
        }

        if (effective.AlwaysSaveOnErrors &&
            (report.ErrorCount > 0 ||
             report.GameRestarts > 0 ||
             report.Outcome is "Faulted" or "Needs Attention"))
        {
            return true;
        }

        if (effective.AlwaysSaveIfTradesOccurred &&
            (report.TotalTradeActions > 0 ||
             report.DeliveryCollections > 0 ||
             report.CharacterSwapsCompleted > 0))
        {
            return true;
        }

        var minSeconds = Math.Max(0, effective.MinDurationMinutes) * 60.0;
        return report.DurationSeconds >= minSeconds;
    }
}

public sealed class SessionReportAccumulator
{
    private const int MaxTradeEvents = 500;
    private const int MaxIncidentEvents = 200;

    private readonly string _id;
    private readonly string _accountName;
    private readonly string _botKind;
    private readonly string _runMode;
    private readonly string _dataSource;
    private readonly string _profile;
    private readonly DateTimeOffset _startedAt;
    private readonly List<SessionTradeEvent> _trades = new();
    private readonly List<SessionIncidentEvent> _incidents = new();

    private int _cyclesExecuted;
    private int _decisionsEvaluated;
    private int _buyOrdersPlaced;
    private int _sellOrdersListed;
    private int _outbidBuysRefreshed;
    private int _deliveryCollections;
    private long _deliveryCoinsCollectedCopper;
    private int _characterSwapsCompleted;
    private int _characterSwapsFailed;
    private int _recoveryAttempts;
    private int _gameRestarts;
    private int _warningCount;
    private int _errorCount;
    private long? _startingWalletCopper;
    private long? _endingWalletCopper;
    private long _estimatedBuyVolumeCopper;
    private int _startingOpenOrders;
    private bool _hasStartingOrders;
    private int _endingOpenOrders;

    public SessionReportAccumulator(
        string id,
        string? accountName,
        BotKind botKind,
        RunMode runMode,
        string dataSource,
        string profile,
        DateTimeOffset startedAt)
    {
        _id = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id;
        _accountName = string.IsNullOrWhiteSpace(accountName) ? "Default Account" : accountName.Trim();
        _botKind = botKind.ToString();
        _runMode = runMode.ToString();
        _dataSource = string.IsNullOrWhiteSpace(dataSource) ? "unknown" : dataSource.Trim();
        _profile = string.IsNullOrWhiteSpace(profile) ? "balanced" : profile.Trim();
        _startedAt = startedAt;
    }

    public void RecordCycle(int decisionsEvaluated)
    {
        _cyclesExecuted++;
        _decisionsEvaluated += Math.Max(0, decisionsEvaluated);
    }

    public void RecordWalletSnapshot(long? walletCopper, int openOrders)
    {
        if (walletCopper.HasValue)
        {
            _startingWalletCopper ??= walletCopper.Value;
            _endingWalletCopper = walletCopper.Value;
        }

        if (!_hasStartingOrders)
        {
            _startingOpenOrders = Math.Max(0, openOrders);
            _hasStartingOrders = true;
        }

        _endingOpenOrders = Math.Max(0, openOrders);
    }

    public void RecordTrade(
        DateTimeOffset timestamp,
        string actionType,
        string itemName,
        long quantity,
        long unitPriceCopper,
        string status,
        string details = "")
    {
        if (string.Equals(actionType, "Buy Order", StringComparison.OrdinalIgnoreCase))
        {
            if (status is "Completed" or "Simulated")
            {
                _buyOrdersPlaced++;
                if (quantity > 0 && unitPriceCopper > 0)
                {
                    _estimatedBuyVolumeCopper += checked(quantity * unitPriceCopper);
                }
            }
        }
        else if (string.Equals(actionType, "Sell Listing", StringComparison.OrdinalIgnoreCase))
        {
            if (status is "Completed" or "Simulated" or "Listed")
            {
                _sellOrdersListed++;
            }
        }
        else if (string.Equals(actionType, "Outbid Refresh", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(actionType, "Cancel Outbid", StringComparison.OrdinalIgnoreCase))
        {
            if (status is "Completed" or "Canceled" or "Refreshed")
            {
                _outbidBuysRefreshed++;
            }
        }

        AddTradeBounded(new SessionTradeEvent(timestamp, actionType, itemName, quantity, unitPriceCopper, status, details));
    }

    public void RecordDeliveryPickup(DateTimeOffset timestamp, long coinsCopper, int itemStacksCount, string details)
    {
        _deliveryCollections++;
        if (coinsCopper > 0)
        {
            _deliveryCoinsCollectedCopper += coinsCopper;
        }

        AddTradeBounded(new SessionTradeEvent(
            timestamp,
            "Delivery Pickup",
            itemStacksCount > 0 ? $"Delivery Box ({itemStacksCount} item stack(s))" : "Delivery Box Coins",
            Math.Max(1, itemStacksCount),
            coinsCopper,
            "Collected",
            details));
    }

    public void RecordCharacterSwap(DateTimeOffset timestamp, bool succeeded, string details)
    {
        if (succeeded)
        {
            _characterSwapsCompleted++;
            AddTradeBounded(new SessionTradeEvent(
                timestamp,
                "Character Swap",
                "Parked Character Rotation",
                1,
                0,
                "Completed",
                details));
        }
        else
        {
            _characterSwapsFailed++;
            RecordIncident(timestamp, "Warning", "Character Swap", details, recovered: false);
        }
    }

    public void RecordRecoveryAttempt(
        DateTimeOffset timestamp,
        string category,
        string message,
        bool recovered,
        bool isGameRestart = false)
    {
        if (isGameRestart)
        {
            _gameRestarts++;
        }
        else
        {
            _recoveryAttempts++;
        }

        RecordIncident(
            timestamp,
            recovered ? "Warning" : "Error",
            category,
            message,
            recovered);
    }

    public void RecordIncident(
        DateTimeOffset timestamp,
        string severity,
        string category,
        string message,
        bool recovered = false)
    {
        if (string.Equals(severity, "Error", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(severity, "Critical", StringComparison.OrdinalIgnoreCase))
        {
            _errorCount++;
        }
        else if (string.Equals(severity, "Warning", StringComparison.OrdinalIgnoreCase))
        {
            _warningCount++;
        }

        if (_incidents.Count < MaxIncidentEvents)
        {
            _incidents.Add(new SessionIncidentEvent(timestamp, severity, category, message, recovered));
        }
    }

    public TradingSessionReport Build(DateTimeOffset endedAt, string outcome, string outcomeSummary)
    {
        var durationSeconds = Math.Max(0, (endedAt - _startedAt).TotalSeconds);
        return new TradingSessionReport(
            Id: _id,
            AccountName: _accountName,
            BotKind: _botKind,
            RunMode: _runMode,
            DataSource: _dataSource,
            Profile: _profile,
            StartedAt: _startedAt,
            EndedAt: endedAt,
            DurationSeconds: durationSeconds,
            Outcome: outcome,
            OutcomeSummary: outcomeSummary,
            CyclesExecuted: _cyclesExecuted,
            DecisionsEvaluated: _decisionsEvaluated,
            BuyOrdersPlaced: _buyOrdersPlaced,
            SellOrdersListed: _sellOrdersListed,
            OutbidBuysRefreshed: _outbidBuysRefreshed,
            DeliveryCollections: _deliveryCollections,
            DeliveryCoinsCollectedCopper: _deliveryCoinsCollectedCopper,
            CharacterSwapsCompleted: _characterSwapsCompleted,
            CharacterSwapsFailed: _characterSwapsFailed,
            RecoveryAttempts: _recoveryAttempts,
            GameRestarts: _gameRestarts,
            WarningCount: _warningCount,
            ErrorCount: _errorCount,
            StartingWalletCopper: _startingWalletCopper,
            EndingWalletCopper: _endingWalletCopper,
            EstimatedBuyVolumeCopper: _estimatedBuyVolumeCopper,
            StartingOpenOrders: _startingOpenOrders,
            EndingOpenOrders: _endingOpenOrders,
            Trades: _trades.ToArray(),
            Incidents: _incidents.ToArray());
    }

    private void AddTradeBounded(SessionTradeEvent entry)
    {
        if (_trades.Count < MaxTradeEvents)
        {
            _trades.Add(entry);
        }
    }
}

public static class SessionReportFormatter
{
    public static string FormatDuration(double totalSeconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, totalSeconds));
        var hours = (int)span.TotalHours;
        return $"{hours:00}:{span.Minutes:00}:{span.Seconds:00}";
    }

    public static string ToMarkdown(TradingSessionReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var sb = new StringBuilder();
        sb.AppendLine($"# Mmopal Trade Session Report — {report.AccountName}");
        sb.AppendLine();
        sb.AppendLine($"- **Report ID:** `{report.Id}`");
        sb.AppendLine($"- **Outcome:** **{report.Outcome}** — {report.OutcomeSummary}");
        sb.AppendLine($"- **Started:** {report.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}  |  **Ended:** {report.EndedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- **Duration:** `{FormatDuration(report.DurationSeconds)}`");
        sb.AppendLine($"- **Session Setup:** {report.BotKind} • {report.RunMode} • Pace `{report.Profile}` • Source `{report.DataSource}`");
        sb.AppendLine();

        sb.AppendLine("## Key Metrics");
        sb.AppendLine();
        sb.AppendLine("| Metric | Value |");
        sb.AppendLine("| --- | --- |");
        sb.AppendLine($"| Cycles Executed | {report.CyclesExecuted:N0} ({report.DecisionsEvaluated:N0} items evaluated) |");
        sb.AppendLine($"| Buy Orders Placed | {report.BuyOrdersPlaced:N0} (Est. volume: {MarketScanItem.FormatCoins(report.EstimatedBuyVolumeCopper)}) |");
        sb.AppendLine($"| Sell Orders Listed | {report.SellOrdersListed:N0} |");
        sb.AppendLine($"| Outbid Buys Refreshed | {report.OutbidBuysRefreshed:N0} |");
        sb.AppendLine($"| Delivery Pickups | {report.DeliveryCollections:N0} ({MarketScanItem.FormatCoins(report.DeliveryCoinsCollectedCopper)}) |");
        sb.AppendLine($"| Character Swaps | {report.CharacterSwapsCompleted:N0} completed / {report.CharacterSwapsFailed:N0} failed |");
        sb.AppendLine($"| Recovery & Restarts | {report.RecoveryAttempts:N0} recoveries / {report.GameRestarts:N0} game restarts |");
        sb.AppendLine($"| Warnings / Errors | {report.WarningCount:N0} warnings / {report.ErrorCount:N0} errors |");
        var startWallet = report.StartingWalletCopper.HasValue ? MarketScanItem.FormatCoins(report.StartingWalletCopper.Value) : "—";
        var endWallet = report.EndingWalletCopper.HasValue ? MarketScanItem.FormatCoins(report.EndingWalletCopper.Value) : "—";
        sb.AppendLine($"| Wallet (Start -> End) | {startWallet} -> {endWallet} |");
        sb.AppendLine($"| Open Orders (Start -> End) | {report.StartingOpenOrders:N0} -> {report.EndingOpenOrders:N0} |");
        sb.AppendLine();

        if (report.Findings is { Count: > 0 } findings)
        {
            sb.AppendLine("## Findings");
            sb.AppendLine();
            foreach (var finding in findings) sb.AppendLine($"- {finding}");
            sb.AppendLine();
            sb.AppendLine($"- **Time:** {DescribeTime(report)}");
            sb.AppendLine($"- **Outcomes:** {DescribeOutcomes(report)}");
            sb.AppendLine();
        }

        sb.AppendLine("## Trade & Session Events");
        sb.AppendLine();
        if (report.Trades.Count == 0)
        {
            sb.AppendLine("_No trade actions, pickups, or swaps recorded during this session._");
        }
        else
        {
            sb.AppendLine("| Time | Type | Item | Qty | Coins | Status | Details |");
            sb.AppendLine("| --- | --- | --- | ---: | ---: | --- | --- |");
            foreach (var t in report.Trades)
            {
                var coins = t.UnitPriceCopper > 0 ? MarketScanItem.FormatCoins(t.UnitPriceCopper) : "—";
                sb.AppendLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "| {0:HH:mm:ss} | {1} | {2} | {3} | {4} | {5} | {6} |",
                    t.Timestamp.ToLocalTime(),
                    EscapePipe(t.ActionType),
                    EscapePipe(t.ItemName),
                    t.Quantity,
                    coins,
                    EscapePipe(t.Status),
                    EscapePipe(t.Details)));
            }
        }

        sb.AppendLine();
        sb.AppendLine("## Incidents & Recovery Log");
        sb.AppendLine();
        if (report.Incidents.Count == 0)
        {
            sb.AppendLine("_No bugs, warnings, or recovery events occurred._");
        }
        else
        {
            sb.AppendLine("| Time | Severity | Category | Recovered | Message |");
            sb.AppendLine("| --- | --- | --- | --- | --- |");
            foreach (var inc in report.Incidents)
            {
                sb.AppendLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "| {0:HH:mm:ss} | {1} | {2} | {3} | {4} |",
                    inc.Timestamp.ToLocalTime(),
                    EscapePipe(inc.Severity),
                    EscapePipe(inc.Category),
                    inc.Recovered ? "Yes" : "No",
                    EscapePipe(inc.Message)));
            }
        }

        return sb.ToString();
    }

    /// <summary>Share of the time per step, for example "buy 38% (12 min), pause 24% (8 min)".</summary>
    public static string DescribeTime(TradingSessionReport report)
    {
        if (report.SecondsByStep is not { Count: > 0 } steps)
        {
            return "not measured";
        }

        var total = Math.Max(1, steps.Values.Sum());
        return string.Join(", ", steps
            .OrderByDescending(step => step.Value)
            .Select(step => string.Create(CultureInfo.InvariantCulture, $"{step.Key} {100 * step.Value / total:0}% ({step.Value / 60:0.#} min)")));
    }

    public static string DescribeOutcomes(TradingSessionReport report) =>
        report.OutcomeCounts is { Count: > 0 } outcomes
            ? string.Join(", ", outcomes.OrderByDescending(pair => pair.Value).Select(pair => $"{pair.Key} {pair.Value}"))
            : "none recorded";

    private static string EscapePipe(string value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
}
