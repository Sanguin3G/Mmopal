using System.Globalization;
using System.Text;
using System.Text.Json;
using Mmopal.Contracts;

namespace Mmopal.Gw2;

public sealed record OperatorSettings(
    string ThemeMode = "System",
    string PauseHotkey = "Ctrl+Shift+P",
    string StopHotkey = "Ctrl+Shift+S",
    string EmergencyStopHotkey = "Ctrl+Shift+X",
    int OverbidBufferMinCopper = 1,
    int OverbidBufferMaxCopper = 5,
    int UndercutBufferMinCopper = 1,
    int UndercutBufferMaxCopper = 5,
    long DefaultOrderQuantity = 1,
    decimal? GoldThresholdNoBuy = null,
    decimal? GoldThresholdRelist = null,
    bool UseCustomShoppingList = true,
    string? CandidatesPath = null,
    string? BlacklistPath = null,
    string? FixturePath = null,
    string? RecordPath = null,
    bool CaptureFrames = false,
    bool EnableDataWarsVelocity = false,
    bool AutoDockSideBySideWhenRunning = true,
    bool UsePortfolioIntelligence = true,
    bool SaveSessionReports = true,
    bool ReportIncludeDryRunAndObserve = false,
    int ReportMinDurationMinutes = 5,
    bool ReportAlwaysSaveIfTradesOccurred = true,
    bool ReportAlwaysSaveOnErrors = true,
    int MaxSavedSessionReports = 100,
    bool AutoListBoughtFlipItems = false,
    decimal AutoRescanHours = 3,
    // Market Scanner filter boxes as typed, so a relaunch (and Auto re-scan) keeps the same filters.
    ScannerUiState? Scanner = null,
    // On launch: sync the GW2 account, Auto-Tune once (first launch only), and scan the market.
    bool AutomateOnLaunch = true,
    // Preset name Auto-Tune last applied; null = never tuned, so the first launch with a wallet tunes once.
    string? LastAutoTunedPreset = null,
    // Smart Auto-Tune at every launch and every Start session (never mid-session). For operators who do not tune by hand.
    bool AutoTuneEachSession = true,
    // Settings the operator typed by hand ("GoldFloor", "MaxBuy", …): Auto-Tune leaves these alone.
    IReadOnlyList<string>? AutoTuneKeeps = null)
{
    public SessionReportPolicySettings ToSessionReportPolicy() => new(
        SaveSessionReports: SaveSessionReports,
        IncludeDryRunAndObserve: ReportIncludeDryRunAndObserve,
        MinDurationMinutes: Math.Clamp(ReportMinDurationMinutes, 0, 1440),
        AlwaysSaveIfTradesOccurred: ReportAlwaysSaveIfTradesOccurred,
        AlwaysSaveOnErrors: ReportAlwaysSaveOnErrors,
        MaxSavedReports: Math.Clamp(MaxSavedSessionReports, 10, 1000));
}

/// <summary>Market Scanner inputs exactly as shown in the UI (text boxes as text, selectors by tag).</summary>
public sealed record ScannerUiState(
    string MinRoi = "10",
    string MaxRoi = "120",
    string MinProfitSilver = "5",
    string MinBuySilver = "10",
    string MaxBuyGold = "50",
    string MinSupply = "100",
    string MinDemand = "100",
    string MinTradesPerDay = "10",
    string? Rarity = null,
    string? Category = null,
    string? Sort = null,
    string? Scope = null,
    bool CheckWalls = true,
    bool ShowTradesPerDay = false,
    bool SkipPriceSpikes = true);

public static class OperatorSettingsFile
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static OperatorSettings LoadOrDefault(string path) =>
        SettingsFileIo.TryReadJson<OperatorSettings>(path, JsonOptions, out var settings) && settings is not null
            ? settings
            : new OperatorSettings();

    public static void Save(string path, OperatorSettings settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(settings);
        SettingsFileIo.WriteAllText(path, JsonSerializer.Serialize(settings, JsonOptions));
    }
}

public static class OperatorListsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static IReadOnlyList<ItemCandidate> LoadShoppingList(string path)
    {
        if (!SettingsFileIo.TryReadJson<List<ItemCandidate>>(path, JsonOptions, out var items))
        {
            return Array.Empty<ItemCandidate>();
        }

        return items ?? [];
    }

    public static void SaveShoppingList(string path, IEnumerable<ItemCandidate> items)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(items);
        var deduplicated = items
            .Where(item => !string.IsNullOrWhiteSpace(item.ItemName))
            .GroupBy(item => ItemNameNormalizer.Normalize(item.ItemName), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .ToList();
        SettingsFileIo.WriteAllText(path, JsonSerializer.Serialize(deduplicated, JsonOptions));
    }

    public static IReadOnlyList<string> LoadBlacklist(string path)
    {
        if (!SettingsFileIo.TryReadJson<List<string>>(path, JsonOptions, out var entries))
        {
            return Array.Empty<string>();
        }

        return (entries ?? [])
            .Where(entry => !string.IsNullOrWhiteSpace(entry))
            .Select(entry => entry.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static void SaveBlacklist(string path, IEnumerable<string> entries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(entries);
        var cleaned = entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry))
            .Select(entry => entry.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(entry => entry, StringComparer.OrdinalIgnoreCase)
            .ToList();
        SettingsFileIo.WriteAllText(path, JsonSerializer.Serialize(cleaned, JsonOptions));
    }

    public static void ExportShoppingListCsv(string path, IEnumerable<ItemCandidate> items)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var sb = new StringBuilder();
        sb.AppendLine("ItemName;Rarity;Quantity;BuyPriceCopper;SellPriceCopper;ROI");
        foreach (var item in items)
        {
            sb.Append(EscapeCsv(item.ItemName)).Append(';')
              .Append(EscapeCsv(item.Rarity ?? "Basic")).Append(';')
              .Append(item.Quantity ?? 1).Append(';')
              .Append(item.BuyPriceCopper?.ToString() ?? string.Empty).Append(';')
              .Append(item.SellPriceCopper?.ToString() ?? string.Empty).Append(';')
              .Append(item.Roi?.ToString("0.##", CultureInfo.InvariantCulture) ?? string.Empty)
              .AppendLine();
        }

        SettingsFileIo.WriteAllText(path, sb.ToString());
    }

    public static void ExportBlacklistCsv(string path, IEnumerable<string> entries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var sb = new StringBuilder();
        sb.AppendLine("ItemName");
        foreach (var entry in entries)
        {
            sb.AppendLine(EscapeCsv(entry));
        }

        SettingsFileIo.WriteAllText(path, sb.ToString());
    }

    private static string EscapeCsv(string value) =>
        value.Contains(';') || value.Contains('"')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
}

/// <summary>File helpers shared by the settings and list stores.</summary>
internal static class SettingsFileIo
{
    /// <summary>
    /// Reads JSON from <paramref name="path"/>. False when the path is blank, the file is missing, or it cannot be
    /// read or parsed (callers then fall back to defaults); true otherwise, even when the JSON is <c>null</c>.
    /// </summary>
    public static bool TryReadJson<T>(string path, JsonSerializerOptions options, out T? value)
    {
        value = default;
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return false;
            }

            value = JsonSerializer.Deserialize<T>(File.ReadAllText(path), options);
            return true;
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            return false;
        }
    }

    /// <summary>Writes <paramref name="contents"/> to the full path of <paramref name="path"/>, creating its folder first.</summary>
    public static void WriteAllText(string path, string contents)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, contents);
    }
}
