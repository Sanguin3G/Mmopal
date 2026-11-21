using System.Globalization;
using System.Text.Json;

namespace Mmopal.Gw2;

public sealed record TradingLimits(decimal MinimumProfitPercentage, decimal? MaximumBuyPriceGold)
{
    public ProfitabilityPolicy ToPolicy() => new(
        MinimumProfitPercentage: MinimumProfitPercentage,
        MaximumBuyPriceCopper: MaximumBuyPriceGold is { } gold
            ? checked((long)decimal.Round(gold * 10_000m, 0, MidpointRounding.AwayFromZero))
            : null);

    public static bool TryParse(string? minimumProfitText, string? maximumBuyGoldText, out TradingLimits limits, out string? error)
    {
        limits = new TradingLimits(0, null);
        error = null;
        var minimum = 0m;
        if (!string.IsNullOrWhiteSpace(minimumProfitText))
        {
            if (!TryDecimal(minimumProfitText, out minimum) || minimum < 0)
            {
                error = "Minimum profit percent must be a number zero or greater.";
                return false;
            }
        }

        decimal? gold = null;
        if (!string.IsNullOrWhiteSpace(maximumBuyGoldText))
        {
            if (!TryDecimal(maximumBuyGoldText, out var parsed) || parsed < 0)
            {
                error = "Maximum buy price must be a gold amount zero or greater, or blank.";
                return false;
            }

            gold = parsed;
        }

        limits = new TradingLimits(minimum, gold);
        try
        {
            _ = limits.ToPolicy();
        }
        catch (OverflowException)
        {
            error = "Maximum buy price is too large to store as copper.";
            limits = new TradingLimits(0, null);
            return false;
        }

        return true;
    }

    private static bool TryDecimal(string text, out decimal value)
    {
        var trimmed = text.Trim();
        var hasDot = trimmed.Contains('.');
        var hasComma = trimmed.Contains(',');
        if (hasDot && !hasComma)
        {
            if (trimmed.Count(ch => ch == '.') >= 2 && IsGroupedNumber(trimmed, '.'))
            {
                return decimal.TryParse(trimmed.Replace(".", string.Empty), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
            }

            return decimal.TryParse(trimmed, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);
        }

        if (hasComma && !hasDot)
        {
            if (IsGroupedNumber(trimmed, ','))
            {
                return decimal.TryParse(trimmed.Replace(",", string.Empty), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
            }

            if (trimmed.Count(ch => ch == ',') == 1)
            {
                return decimal.TryParse(trimmed.Replace(',', '.'), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);
            }

            value = 0;
            return false;
        }

        if (hasDot && hasComma)
        {
            // Whichever separator comes last is the decimal point: "1,234.5" (US) or "1.234,5" (EU).
            return trimmed.LastIndexOf('.') > trimmed.LastIndexOf(',')
                ? TryParseGroupedWithFraction(trimmed, decimalSeparator: '.', groupSeparator: ',', out value)
                : TryParseGroupedWithFraction(trimmed, decimalSeparator: ',', groupSeparator: '.', out value);
        }

        return decimal.TryParse(trimmed, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryParseGroupedWithFraction(string text, char decimalSeparator, char groupSeparator, out decimal value)
    {
        var decimalIndex = text.LastIndexOf(decimalSeparator);
        var integerPart = text[..decimalIndex];
        var fractionPart = text[(decimalIndex + 1)..];
        if (!IsGroupedNumber(integerPart, groupSeparator) || fractionPart.Length == 0 || !fractionPart.All(char.IsDigit))
        {
            value = 0;
            return false;
        }

        return decimal.TryParse(
            integerPart.Replace(groupSeparator.ToString(), string.Empty) + "." + fractionPart,
            NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture,
            out value);
    }

    private static bool IsGroupedNumber(string text, char separator)
    {
        var span = text.AsSpan();
        if (span.Length > 0 && (span[0] == '+' || span[0] == '-'))
        {
            span = span[1..];
        }

        var parts = span.ToString().Split(separator);
        if (parts.Length < 2) return false;
        if (parts[0].Length is < 1 or > 3 || !parts[0].All(char.IsDigit)) return false;
        for (var i = 1; i < parts.Length; i++)
        {
            if (parts[i].Length != 3 || !parts[i].All(char.IsDigit)) return false;
        }

        return true;
    }
}

public static class TradingLimitsFile
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static TradingLimits LoadOrDefault(string path) =>
        SettingsFileIo.TryReadJson<TradingLimits>(path, JsonOptions, out var limits) && limits is not null
            ? limits
            : new TradingLimits(0, null);

    public static void Save(string path, TradingLimits limits)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(limits);
        SettingsFileIo.WriteAllText(path, JsonSerializer.Serialize(limits, JsonOptions));
    }
}
