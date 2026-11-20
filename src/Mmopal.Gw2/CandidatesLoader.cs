using System.Globalization;
using System.Text.Json;
using Mmopal.Contracts;
using Mmopal.Infrastructure;

namespace Mmopal.Gw2;

/// <summary>
/// Loads candidate items and blacklist entries from JSON or delimited CSV files for trading evaluation.
/// </summary>
public static class CandidatesLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public static async Task<IReadOnlyList<ItemCandidate>> LoadCandidatesAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Candidates file was not found.", fullPath);
        }

        var text = await File.ReadAllTextAsync(fullPath, cancellationToken);
        var trimmed = text.TrimStart();
        if (trimmed.Length == 0)
        {
            return Array.Empty<ItemCandidate>();
        }

        if (trimmed.StartsWith('['))
        {
            return JsonSerializer.Deserialize<List<ItemCandidate>>(trimmed, JsonOptions)
                ?? [];
        }

        if (trimmed.StartsWith('{'))
        {
            using var doc = JsonDocument.Parse(trimmed);
            if (doc.RootElement.TryGetProperty("candidates", out var prop) && prop.ValueKind == JsonValueKind.Array)
            {
                return prop.Deserialize<List<ItemCandidate>>(JsonOptions) ?? [];
            }

            throw new InvalidDataException("Candidates JSON object must contain a 'candidates' array.");
        }

        var delimiter = DetectDelimiter(trimmed);
        var rows = DelimitedTextParser.Parse(text, delimiter);
        var candidates = new List<ItemCandidate>(rows.Count);
        foreach (var row in rows)
        {
            var name = FirstValue(row, "ItemName", "Name", "item_name");
            if (string.IsNullOrWhiteSpace(name)) continue;

            candidates.Add(new ItemCandidate(
                ItemName: name.Trim(),
                Rarity: NullIfBlank(FirstValue(row, "Rarity")),
                Quantity: ParseLong(FirstValue(row, "Quantity", "Qty")),
                MinimumProfitPercentage: ParseDecimal(FirstValue(row, "MinimumProfitPercentage", "MinProfit", "MinProfitPct")),
                MaximumPriceCopper: ParseLong(FirstValue(row, "MaximumPriceCopper", "MaxPriceCopper", "MaxPrice")),
                Roi: ParseDecimal(FirstValue(row, "Roi", "ROI")),
                SellPriceCopper: ParseLong(FirstValue(row, "SellPriceCopper", "SellPrice", "Sell")),
                BuyPriceCopper: ParseLong(FirstValue(row, "BuyPriceCopper", "BuyPrice", "Buy")),
                ItemId: ParseInt(FirstValue(row, "ItemId", "Id", "item_id"))));
        }

        return candidates;
    }

    public static async Task<IReadOnlyList<string>> LoadBlacklistAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Blacklist file was not found.", fullPath);
        }

        var text = await File.ReadAllTextAsync(fullPath, cancellationToken);
        var trimmed = text.TrimStart();
        if (trimmed.Length == 0)
        {
            return Array.Empty<string>();
        }

        if (trimmed.StartsWith('['))
        {
            return (JsonSerializer.Deserialize<List<string>>(trimmed, JsonOptions) ?? [])
                .Where(entry => !string.IsNullOrWhiteSpace(entry))
                .Select(entry => entry.Trim())
                .ToArray();
        }

        var entries = new List<string>();
        using var reader = new StringReader(text);
        var first = true;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            var value = line.Trim().Trim('"');
            if (value.Length == 0 || value.StartsWith('#')) continue;
            if (first && (value.Equals("ItemName", StringComparison.OrdinalIgnoreCase) || value.Equals("Name", StringComparison.OrdinalIgnoreCase)))
            {
                first = false;
                continue;
            }

            first = false;
            entries.Add(value);
        }

        return entries;
    }

    private static char DetectDelimiter(string text)
    {
        var newline = text.IndexOfAny(['\r', '\n']);
        var header = newline >= 0 ? text[..newline] : text;
        return header.Contains(';') ? ';' : ',';
    }

    private static string? FirstValue(IReadOnlyDictionary<string, string?> row, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (row.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static long? ParseLong(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        if (long.TryParse(trimmed.Replace(",", string.Empty), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        // Support GW2BLTC / GW2Profits formatted coin strings like "1g 23s 45c" or "45s 10c".
        long total = 0;
        var matchedAny = false;
        foreach (var token in trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var suffix = char.ToLowerInvariant(token[^1]);
            var numberPart = token[..^1].Replace(",", string.Empty);
            if (!long.TryParse(numberPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var component) || component < 0)
            {
                return null;
            }

            if (suffix == 'g') { total += component * 10_000; matchedAny = true; }
            else if (suffix == 's') { total += component * 100; matchedAny = true; }
            else if (suffix == 'c') { total += component; matchedAny = true; }
            else return null;
        }

        return matchedAny ? total : null;
    }

    private static int? ParseInt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return int.TryParse(value.Trim().Replace(",", string.Empty), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static decimal? ParseDecimal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var cleaned = value.Trim().TrimEnd('%').Trim();
        return decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }
}
