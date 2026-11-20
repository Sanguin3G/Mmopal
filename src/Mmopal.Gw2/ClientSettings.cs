using System.Globalization;
using System.Text.Json;

namespace Mmopal.Gw2;

public sealed record Gw2ClientSettings(
    string? ExecutablePath = null,
    string LaunchArguments = "-autologin -windowed",
    int WindowWidth = 1024,
    int WindowHeight = 768,
    bool AutoPrepareOnScreenStart = true,
    bool AutoLaunchIfNotRunning = false,
    int? SwapCharacterIntervalMinutes = null,
    int SwapCharacterJitterMinutes = 15,
    bool RandomizeCharacterSelection = true,
    int MaxLaunchAttempts = 2,
    int WindowWaitTimeoutSeconds = 45,
    int? MaxSessionDurationMinutes = 600,
    int ParkedCharacterCount = 4,
    string InteractKeybind = "F",
    string TradingPostKeybind = "O",
    bool ProtectSellListingsFromCancel = true,
    string LatencyProfile = "HighPing",
    int UiLoadWaitMs = 7500,
    int ActionSettleDelayMs = 900,
    int ApiTimeoutSeconds = 60,
    string LaunchPlatform = "ArenaNet",
    int MaxRecoveryRetries = 3,
    bool AutoRestartGameOnFailure = true,
    int MaxGameRestartsPerSession = 2,
    bool AutoCloseGameOnSessionCap = true,
    bool AutoCloseAppOnSessionCap = true,
    bool ShutdownPcOnSessionCap = false,
    int PcShutdownCountdownSeconds = 60,
    // How buy and sell prices are set in the order dialog: TypeExact (type the exact gold/silver/copper from the
    // live top bid / lowest ask and verify the total; default) or ClickRow (click the top row, tap the spinner).
    string PriceEntryMode = "TypeExact")
{
    public const string PriceEntryClickRow = "ClickRow";
    public const string PriceEntryTypeExact = "TypeExact";

    public bool TypesExactPrice => string.Equals(PriceEntryMode, PriceEntryTypeExact, StringComparison.OrdinalIgnoreCase);

    public const string DefaultExecutableName = "Gw2-64.exe";
    public const string DefaultWindowName = "Guild Wars 2";
    public const string PlatformArenaNet = "ArenaNet";
    public const string PlatformSteam = "Steam";
    public const string SteamLaunchUri = "steam://rungameid/1284210";

    public bool IsSteamPlatform =>
        string.Equals(LaunchPlatform, PlatformSteam, StringComparison.OrdinalIgnoreCase) ||
        (!string.IsNullOrWhiteSpace(ExecutablePath) &&
         ExecutablePath.Trim().StartsWith("steam://", StringComparison.OrdinalIgnoreCase));

    public string? ResolveEffectiveLaunchTarget(Func<string?>? detectStandalonePath = null)
    {
        var trimmed = string.IsNullOrWhiteSpace(ExecutablePath) ? null : ExecutablePath.Trim();
        if (IsSteamPlatform)
        {
            return trimmed is not null && trimmed.StartsWith("steam://", StringComparison.OrdinalIgnoreCase)
                ? trimmed
                : SteamLaunchUri;
        }

        return trimmed ?? detectStandalonePath?.Invoke();
    }

    public static bool TryParseSwapHours(string? hoursText, out int? intervalMinutes, out string? error)
    {
        intervalMinutes = null;
        error = null;
        if (string.IsNullOrWhiteSpace(hoursText))
        {
            return true;
        }

        var normalized = hoursText.Trim().Replace(',', '.');
        if (!decimal.TryParse(normalized, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var hours) || hours < 0)
        {
            error = "Character swap interval must be a number of hours zero or greater, or blank.";
            return false;
        }

        if (hours == 0)
        {
            return true;
        }

        var minutes = decimal.Round(hours * 60m, 0, MidpointRounding.AwayFromZero);
        if (minutes < 5 || minutes > 24 * 60)
        {
            error = "Character swap interval must be between 0.1 and 24 hours (or blank to disable).";
            return false;
        }

        intervalMinutes = (int)minutes;
        return true;
    }

    public string FormatSwapHours() =>
        SwapCharacterIntervalMinutes is > 0
            ? (SwapCharacterIntervalMinutes.Value / 60m).ToString("0.##", CultureInfo.InvariantCulture)
            : string.Empty;

    public static bool TryParseMaxSessionHours(string? hoursText, out int? durationMinutes, out string? error)
    {
        durationMinutes = null;
        error = null;
        if (string.IsNullOrWhiteSpace(hoursText))
        {
            return true;
        }

        var normalized = hoursText.Trim().Replace(',', '.');
        if (!decimal.TryParse(normalized, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var hours) || hours < 0)
        {
            error = "Max session duration must be a number of hours zero or greater, or blank.";
            return false;
        }

        if (hours == 0)
        {
            return true;
        }

        if (hours < 0.25m || hours > 24m)
        {
            error = "Max session duration must be between 0.25 and 24 hours (or blank to disable).";
            return false;
        }

        durationMinutes = (int)decimal.Round(hours * 60m, 0, MidpointRounding.AwayFromZero);
        return true;
    }

    public string FormatMaxSessionHours() =>
        MaxSessionDurationMinutes is > 0
            ? (MaxSessionDurationMinutes.Value / 60m).ToString("0.##", CultureInfo.InvariantCulture)
            : string.Empty;
}

public static class Gw2ClientSettingsFile
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static Gw2ClientSettings LoadOrDefault(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return new Gw2ClientSettings();
            }

            return JsonSerializer.Deserialize<Gw2ClientSettings>(File.ReadAllText(path), JsonOptions)
                   ?? new Gw2ClientSettings();
        }
        catch (JsonException)
        {
            return new Gw2ClientSettings();
        }
        catch (IOException)
        {
            return new Gw2ClientSettings();
        }
    }

    public static void Save(string path, Gw2ClientSettings settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(settings);
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, JsonSerializer.Serialize(settings, JsonOptions));
    }
}

public sealed record Gw2ClientPrepareResult(
    bool WindowReady,
    bool Launched,
    bool AlreadyRunning,
    string Message);

public interface IGw2ClientPreparer
{
    Task<Gw2ClientPrepareResult> PrepareAsync(
        Gw2ClientSettings settings,
        bool allowLaunch,
        CancellationToken cancellationToken = default);

    Task<Gw2ClientPrepareResult> RestartAsync(
        Gw2ClientSettings settings,
        CancellationToken cancellationToken = default) =>
        PrepareAsync(settings, allowLaunch: true, cancellationToken);

    Task<Gw2ClientPrepareResult> CloseGameAsync(
        Gw2ClientSettings settings,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new Gw2ClientPrepareResult(false, false, false, "Game close not supported by this preparer."));
}

public sealed class AfkCharacterSwapSchedule
{
    private readonly int? _intervalMinutes;
    private readonly int _jitterMinutes;
    private readonly bool _randomizeCharacter;
    private readonly Func<int, int, int> _nextRandom;

    public AfkCharacterSwapSchedule(
        int? intervalMinutes,
        int jitterMinutes = 15,
        bool randomizeCharacter = true,
        DateTimeOffset? startedAt = null,
        Func<int, int, int>? nextRandom = null)
    {
        _intervalMinutes = intervalMinutes is > 0 ? intervalMinutes : null;
        _jitterMinutes = Math.Max(0, jitterMinutes);
        _randomizeCharacter = randomizeCharacter;
        _nextRandom = nextRandom ?? Random.Shared.Next;
        var origin = startedAt ?? DateTimeOffset.UtcNow;
        NextSwapAt = ComputeNextSwap(origin);
    }

    public bool IsEnabled => _intervalMinutes.HasValue;

    public bool RandomizeCharacter => _randomizeCharacter;

    public DateTimeOffset? NextSwapAt { get; private set; }

    public int SwapCount { get; private set; }

    public bool IsDue(DateTimeOffset now) =>
        NextSwapAt.HasValue && now >= NextSwapAt.Value;

    public void MarkSwapped(DateTimeOffset now)
    {
        SwapCount++;
        NextSwapAt = ComputeNextSwap(now);
    }

    public string Describe(DateTimeOffset now)
    {
        if (!IsEnabled || !NextSwapAt.HasValue)
        {
            return "AFK character swap disabled.";
        }

        var remaining = NextSwapAt.Value - now;
        var mode = _randomizeCharacter ? "random character" : "next character";
        if (remaining <= TimeSpan.Zero)
        {
            return $"AFK character swap due now ({mode}).";
        }

        return $"Next AFK swap ({mode}) in {Math.Ceiling(remaining.TotalMinutes):0}m at {NextSwapAt.Value.ToLocalTime():t}.";
    }

    private DateTimeOffset? ComputeNextSwap(DateTimeOffset from)
    {
        if (!_intervalMinutes.HasValue)
        {
            return null;
        }

        var baseMinutes = _intervalMinutes.Value;
        var effectiveJitter = Math.Min(_jitterMinutes, Math.Max(0, baseMinutes / 2));
        var offset = effectiveJitter > 0
            ? _nextRandom(-effectiveJitter, effectiveJitter + 1)
            : 0;
        var finalMinutes = Math.Max(1, baseMinutes + offset);
        return from.AddMinutes(finalMinutes);
    }
}

public sealed class SessionDurationCap
{
    private readonly int? _durationMinutes;

    public SessionDurationCap(int? durationMinutes, DateTimeOffset? startedAt = null)
    {
        _durationMinutes = durationMinutes is > 0 ? durationMinutes : null;
        StartedAt = startedAt ?? DateTimeOffset.UtcNow;
        ExpiresAt = _durationMinutes.HasValue
            ? StartedAt.AddMinutes(_durationMinutes.Value)
            : null;
    }

    public bool IsEnabled => _durationMinutes.HasValue;

    public DateTimeOffset StartedAt { get; }

    public int? DurationMinutes => _durationMinutes;

    public DateTimeOffset? ExpiresAt { get; }

    public bool IsExpired(DateTimeOffset now) =>
        ExpiresAt.HasValue && now >= ExpiresAt.Value;

    public string Describe(DateTimeOffset now)
    {
        if (!IsEnabled || !ExpiresAt.HasValue)
        {
            return "Max session cap disabled";
        }

        var remaining = ExpiresAt.Value - now;
        if (remaining <= TimeSpan.Zero)
        {
            return "Session cap reached";
        }

        var hours = (int)remaining.TotalHours;
        var mins = remaining.Minutes;
        return hours > 0
            ? $"Stops in {hours}h {mins:00}m ({ExpiresAt.Value.ToLocalTime():t})"
            : $"Stops in {Math.Ceiling(remaining.TotalMinutes):0}m ({ExpiresAt.Value.ToLocalTime():t})";
    }
}

public sealed record Gw2LatencyPreset(
    string Id,
    string Label,
    int UiLoadWaitMs,
    int ActionSettleDelayMs,
    int ApiTimeoutSeconds,
    string Description);

public static class Gw2LatencyPresets
{
    public static IReadOnlyList<Gw2LatencyPreset> All { get; } =
    [
        new Gw2LatencyPreset(
            "Low",
            "Low Ping (< 100ms)",
            3000,
            350,
            30,
            "Fast local/VPN latency (e.g. Mudfish or EU local). Snappy UI waits."),
        new Gw2LatencyPreset(
            "Normal",
            "Normal (100–200ms)",
            4500,
            550,
            40,
            "Moderate latency with comfortable buffer for Trading Post tabs."),
        new Gw2LatencyPreset(
            "HighPing",
            "High Ping / SEA→EU (200–320ms)",
            7500,
            900,
            60,
            "Recommended without Mudfish (220–280ms ping). Patient UI waits & 60s API timeout."),
        new Gw2LatencyPreset(
            "Custom",
            "Custom",
            7500,
            900,
            60,
            "Use your own UI Load Wait, Action Delay, and API Timeout values.")
    ];

    public static Gw2LatencyPreset? FindById(string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : All.FirstOrDefault(p => p.Id.Equals(id.Trim(), StringComparison.OrdinalIgnoreCase));
}

public static class TradingPaceDescriptions
{
    public static string Describe(string? profileTag) => profileTag?.Trim().ToLowerInvariant() switch
    {
        "safe" => "Safe: 2 actions/pass · 90s interval — best for overnight & anti-throttle",
        "fast" => "Fast: 5 actions/pass · 25s interval — high turnover for Reset & Prime Time",
        _ => "Balanced: 3 actions/pass · 50s interval — optimal for 5-min GW2 API cache"
    };
}
