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
    bool AutoTuneEachSession = true)
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

    public static OperatorSettings LoadOrDefault(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return new OperatorSettings();
            }

            return JsonSerializer.Deserialize<OperatorSettings>(File.ReadAllText(path), JsonOptions)
                   ?? new OperatorSettings();
        }
        catch (JsonException)
        {
            return new OperatorSettings();
        }
        catch (IOException)
        {
            return new OperatorSettings();
        }
    }

    public static void Save(string path, OperatorSettings settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(settings);
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, JsonSerializer.Serialize(settings, JsonOptions));
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
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return Array.Empty<ItemCandidate>();
            }

            return JsonSerializer.Deserialize<List<ItemCandidate>>(File.ReadAllText(path), JsonOptions)
                   ?? [];
        }
        catch (JsonException)
        {
            return Array.Empty<ItemCandidate>();
        }
        catch (IOException)
        {
            return Array.Empty<ItemCandidate>();
        }
    }

    public static void SaveShoppingList(string path, IEnumerable<ItemCandidate> items)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(items);
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var deduplicated = items
            .Where(item => !string.IsNullOrWhiteSpace(item.ItemName))
            .GroupBy(item => ItemNameNormalizer.Normalize(item.ItemName), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .ToList();
        File.WriteAllText(fullPath, JsonSerializer.Serialize(deduplicated, JsonOptions));
    }

    public static IReadOnlyList<string> LoadBlacklist(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return Array.Empty<string>();
            }

            return (JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path), JsonOptions) ?? [])
                .Where(entry => !string.IsNullOrWhiteSpace(entry))
                .Select(entry => entry.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
        catch (IOException)
        {
            return Array.Empty<string>();
        }
    }

    public static void SaveBlacklist(string path, IEnumerable<string> entries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(entries);
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var cleaned = entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry))
            .Select(entry => entry.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(entry => entry, StringComparer.OrdinalIgnoreCase)
            .ToList();
        File.WriteAllText(fullPath, JsonSerializer.Serialize(cleaned, JsonOptions));
    }

    public static void ExportShoppingListCsv(string path, IEnumerable<ItemCandidate> items)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var sb = new StringBuilder();
        sb.AppendLine("ItemName;Rarity;Quantity;BuyPriceCopper;SellPriceCopper;ROI");
        foreach (var item in items)
        {
            sb.Append(EscapeCsv(item.ItemName)).Append(';')
              .Append(EscapeCsv(item.Rarity ?? "Basic")).Append(';')
              .Append(item.Quantity ?? 1).Append(';')
              .Append(item.BuyPriceCopper?.ToString() ?? string.Empty).Append(';')
              .Append(item.SellPriceCopper?.ToString() ?? string.Empty).Append(';')
              .Append(item.Roi?.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty)
              .AppendLine();
        }

        File.WriteAllText(fullPath, sb.ToString());
    }

    public static void ExportBlacklistCsv(string path, IEnumerable<string> entries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var sb = new StringBuilder();
        sb.AppendLine("ItemName");
        foreach (var entry in entries)
        {
            sb.AppendLine(EscapeCsv(entry));
        }

        File.WriteAllText(fullPath, sb.ToString());
    }

    private static string EscapeCsv(string value) =>
        value.Contains(';') || value.Contains('"')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
}
