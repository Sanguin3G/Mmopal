using System.Text;
using Mmopal.Contracts;

namespace Mmopal.Gw2;

public static class CurrencyMath
{
    public static long ToCopper(long gold, long silver, long copper)
    {
        if (gold < 0 || silver is < 0 or > 99 || copper is < 0 or > 99)
        {
            throw new ArgumentOutOfRangeException(nameof(copper), "Guild Wars 2 currency components are out of range.");
        }

        checked
        {
            return gold * 10_000 + silver * 100 + copper;
        }
    }

    public static (long Gold, int Silver, int Copper) FromCopper(long totalCopper)
    {
        if (totalCopper < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(totalCopper));
        }

        return (totalCopper / 10_000, (int)((totalCopper % 10_000) / 100), (int)(totalCopper % 100));
    }
}

public static class ItemNameNormalizer
{
    public static string Normalize(string? value)
    {
        return string.Join(' ', (value ?? string.Empty)
            .Normalize(NormalizationForm.FormKC)
            .Trim()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToUpperInvariant();
    }
}

public sealed class TransactionIndex
{
    private readonly Dictionary<string, List<Transaction>> _byItem = new(StringComparer.OrdinalIgnoreCase);

    public TransactionIndex(IEnumerable<Transaction> transactions)
    {
        foreach (var transaction in transactions)
        {
            var key = ItemNameNormalizer.Normalize(transaction.ItemName);
            if (!_byItem.TryGetValue(key, out var list))
            {
                list = new List<Transaction>();
                _byItem[key] = list;
            }

            list.Add(transaction);
        }
    }

    public IReadOnlyList<Transaction> Find(string itemName) =>
        _byItem.TryGetValue(ItemNameNormalizer.Normalize(itemName), out var transactions)
            ? transactions
            : Array.Empty<Transaction>();

    public bool Contains(string itemName, TransactionType type) =>
        Find(itemName).Any(transaction => transaction.Type == type);

    public Transaction? FindFirst(string itemName, TransactionType type) =>
        Find(itemName).FirstOrDefault(transaction => transaction.Type == type);
}

public sealed record ProfitabilityPolicy(
    int ListingFeeBasisPoints = 500,
    int ExchangeFeeBasisPoints = 1_000,
    decimal MinimumProfitPercentage = 0,
    long? MaximumBuyPriceCopper = null,
    bool RoundFeesToNearestCopper = true);

public sealed class ProfitabilityEngine
{
    public ProfitabilityResult Evaluate(
        long buyPriceCopper,
        long sellPriceCopper,
        ProfitabilityPolicy policy,
        decimal? itemMinimumProfitPercentage = null)
    {
        if (buyPriceCopper <= 0 || sellPriceCopper <= 0)
        {
            return Invalid(buyPriceCopper, sellPriceCopper, "Buy and sell prices must be positive.");
        }

        if (policy.MaximumBuyPriceCopper.HasValue && buyPriceCopper > policy.MaximumBuyPriceCopper.Value)
        {
            return Invalid(buyPriceCopper, sellPriceCopper, "Buy price exceeds the configured maximum.");
        }

        var listingFee = CalculateFee(sellPriceCopper, policy.ListingFeeBasisPoints, policy.RoundFeesToNearestCopper);
        var exchangeFee = CalculateFee(sellPriceCopper, policy.ExchangeFeeBasisPoints, policy.RoundFeesToNearestCopper);
        var netSale = sellPriceCopper - listingFee - exchangeFee;
        var profit = netSale - buyPriceCopper;
        var percentage = profit * 100m / buyPriceCopper;
        var minimum = itemMinimumProfitPercentage ?? policy.MinimumProfitPercentage;

        return new ProfitabilityResult(
            IsValid: true,
            IsProfitable: profit > 0 && percentage > minimum,
            BuyPriceCopper: buyPriceCopper,
            SellPriceCopper: sellPriceCopper,
            ListingFeeCopper: listingFee,
            ExchangeFeeCopper: exchangeFee,
            NetSaleCopper: netSale,
            ProfitCopper: profit,
            ProfitPercentage: percentage,
            Reason: UnprofitableReason(profit, percentage, minimum));
    }

    private static string? UnprofitableReason(long profit, decimal percentage, decimal minimum)
    {
        if (profit <= 0) return "Net sale value does not exceed the buy price.";
        if (percentage <= minimum) return "Profit percentage is below the configured threshold.";
        return null;
    }

    private static long CalculateFee(long price, int basisPoints, bool round)
    {
        if (price <= 0 || basisPoints <= 0) return 0;
        var amount = price * basisPoints / 10_000m;
        var fee = round ? (long)Math.Floor(amount + 0.5m) : (long)Math.Floor(amount);
        return Math.Max(1L, fee);
    }

    private static ProfitabilityResult Invalid(long buy, long sell, string reason) => new(
        false, false, buy, sell, 0, 0, 0, 0, 0, reason);
}

public sealed class BlacklistMatcher
{
    private readonly HashSet<string> _exact;
    private readonly IReadOnlyList<string> _wildcards;

    public BlacklistMatcher(IEnumerable<string> entries)
    {
        var normalized = entries
            .Select(ItemNameNormalizer.Normalize)
            .Where(value => value.Length > 0)
            .ToArray();

        _exact = normalized.Where(value => !value.Contains('*')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _wildcards = normalized.Where(value => value.Contains('*')).ToArray();
    }

    public bool IsBlocked(string? itemName)
    {
        var normalized = ItemNameNormalizer.Normalize(itemName);
        if (_exact.Contains(normalized))
        {
            return true;
        }

        return _wildcards.Any(pattern => WildcardMatch(normalized, pattern));
    }

    private static bool WildcardMatch(string value, string pattern)
    {
        var valueIndex = 0;
        var patternIndex = 0;
        var starIndex = -1;
        var matchIndex = 0;

        while (valueIndex < value.Length)
        {
            if (patternIndex < pattern.Length && pattern[patternIndex] != '*' && pattern[patternIndex] == value[valueIndex])
            {
                valueIndex++;
                patternIndex++;
            }
            else if (patternIndex < pattern.Length && pattern[patternIndex] == '*')
            {
                starIndex = patternIndex++;
                matchIndex = valueIndex;
            }
            else if (starIndex >= 0)
            {
                patternIndex = starIndex + 1;
                valueIndex = ++matchIndex;
            }
            else
            {
                return false;
            }
        }

        while (patternIndex < pattern.Length && pattern[patternIndex] == '*') patternIndex++;
        return patternIndex == pattern.Length;
    }
}

public sealed class CandidatePlanner
{
    private readonly ProfitabilityEngine _profitability;

    public CandidatePlanner(ProfitabilityEngine? profitability = null) => _profitability = profitability ?? new ProfitabilityEngine();

    public IReadOnlyList<ItemCandidate> Plan(
        IEnumerable<ItemCandidate> candidates,
        TransactionIndex transactions,
        BlacklistMatcher blacklist,
        ProfitabilityPolicy policy,
        bool preserveInputOrder = true)
    {
        var results = Evaluate(candidates, transactions, blacklist, policy)
            .Where(decision => decision.Action is not null)
            .Select(decision => decision.Candidate)
            .ToList();

        if (!preserveInputOrder)
        {
            results.Sort((left, right) =>
                (right.Roi ?? 0m).CompareTo(left.Roi ?? 0m));
        }

        return results;
    }

    public IReadOnlyList<CandidateDecision> Evaluate(
        IEnumerable<ItemCandidate> candidates,
        TransactionIndex transactions,
        BlacklistMatcher blacklist,
        ProfitabilityPolicy policy)
    {
        var decisions = new List<CandidateDecision>();
        foreach (var candidate in candidates)
        {
            if (blacklist.IsBlocked(candidate.ItemName))
            {
                decisions.Add(CandidateDecision.Skip(candidate, "Item is blacklisted."));
                continue;
            }

            if (transactions.Contains(candidate.ItemName, TransactionType.Buy))
            {
                decisions.Add(CandidateDecision.Skip(candidate, "A buy order already exists for this item."));
                continue;
            }

            if (!candidate.BuyPriceCopper.HasValue || !candidate.SellPriceCopper.HasValue)
            {
                decisions.Add(CandidateDecision.Skip(candidate, "Buy and sell prices are incomplete."));
                continue;
            }

            var maximumPrice = policy.MaximumBuyPriceCopper;
            if (candidate.MaximumPriceCopper.HasValue)
            {
                maximumPrice = maximumPrice.HasValue
                    ? Math.Min(maximumPrice.Value, candidate.MaximumPriceCopper.Value)
                    : candidate.MaximumPriceCopper.Value;
            }

            var effectivePolicy = policy with { MaximumBuyPriceCopper = maximumPrice };
            var profitability = _profitability.Evaluate(
                candidate.BuyPriceCopper.Value,
                candidate.SellPriceCopper.Value,
                effectivePolicy,
                candidate.MinimumProfitPercentage);
            if (!profitability.IsValid || !profitability.IsProfitable)
            {
                decisions.Add(CandidateDecision.Skip(candidate, profitability.Reason ?? "Candidate is not profitable.", profitability));
                continue;
            }

            var quantity = candidate.Quantity ?? 1;
            if (quantity <= 0)
            {
                decisions.Add(CandidateDecision.Skip(candidate, "Quantity must be positive.", profitability));
                continue;
            }

            decisions.Add(CandidateDecision.Accept(
                candidate,
                new TradingAction(
                    TradingActionKind.PlaceBuyOrder,
                    candidate.ItemName,
                    candidate.BuyPriceCopper.Value,
                    quantity,
                    "Candidate passed blacklist, existing-order, price, and profitability checks."),
                profitability));
        }

        return decisions;
    }
}

public enum TradingActionKind
{
    PlaceBuyOrder,
    PlaceSellOrder,
    CancelBuyOrder,
    CancelSellOrder
}

public sealed record TradingAction(
    TradingActionKind Kind,
    string ItemName,
    long PriceCopper,
    long Quantity,
    string Reason)
{
    public string Signature => $"{Kind}|{ItemNameNormalizer.Normalize(ItemName)}|{PriceCopper}|{Quantity}";
}

public enum CandidateDecisionKind
{
    Accepted,
    Skipped
}

public sealed record CandidateDecision(
    ItemCandidate Candidate,
    CandidateDecisionKind Kind,
    TradingAction? Action,
    ProfitabilityResult? Profitability,
    string Reason)
{
    public static CandidateDecision Accept(ItemCandidate candidate, TradingAction action, ProfitabilityResult profitability) =>
        new(candidate, CandidateDecisionKind.Accepted, action, profitability, action.Reason);

    public static CandidateDecision Skip(ItemCandidate candidate, string reason, ProfitabilityResult? profitability = null) =>
        new(candidate, CandidateDecisionKind.Skipped, null, profitability, reason);
}
