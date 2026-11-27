namespace Mmopal.Contracts;

public sealed record Gw2LedgerTransaction(
    long Id,
    TransactionType Kind,
    int ItemId,
    string ItemName,
    long UnitPriceCopper,
    long Quantity,
    DateTimeOffset CreatedAt,
    DateTimeOffset PurchasedAt);

public sealed record Gw2DeliveryItem(
    int ItemId,
    long Count);

public sealed record Gw2DeliverySnapshot(
    long CoinsCopper,
    IReadOnlyList<Gw2DeliveryItem> Items,
    DateTimeOffset ObservedAt);

public sealed record Gw2ActiveOrder(
    long Id,
    TransactionType Kind,
    int ItemId,
    string ItemName,
    long UnitPriceCopper,
    long Quantity,
    DateTimeOffset CreatedAt,
    long? MarketHighestBuyCopper,
    long? MarketLowestSellCopper,
    string BookStatus);

public sealed record Gw2EquitySnapshot(
    DateTimeOffset CapturedAt,
    long WalletCopper,
    long DeliveryCopper,
    long DeliveryItemsCopper,
    long ActiveBuysCopper,
    long ActiveSellsNetCopper,
    long TotalEquityCopper,
    int OpenBuyCount,
    int OpenSellCount);

public sealed record Gw2CachedItemInfo(
    int ItemId,
    string Name,
    string Rarity,
    string ItemType,
    long VendorValueCopper,
    DateTimeOffset UpdatedAt);

public sealed record Gw2ApiKeyEntry(
    string Id,
    string ApiKey,
    string AccountName,
    string CustomName,
    string Status,
    IReadOnlyList<string> Permissions,
    long? LastWalletCopper,
    DateTimeOffset? LastVerifiedAt,
    string LaunchPlatform = "",
    int? ParkedCharacters = null,
    // Gold the player wants for playing, per account: kept in the wallet, and freed by a time of day ("HH:mm").
    decimal? KeepGold = null,
    decimal? FreeGold = null,
    string? FreeGoldBy = null)
{
    public string MaskedKey => ApiKey.Length > 18 ? ApiKey[..18] + "..." : ApiKey;

    public string DisplayLabel => string.IsNullOrWhiteSpace(CustomName)
        ? (string.IsNullOrWhiteSpace(AccountName) ? "Account" : AccountName)
        : $"{AccountName} ({CustomName})";
}

public sealed record Gw2ApiKeyVaultData(
    string? ActiveKeyId,
    IReadOnlyList<Gw2ApiKeyEntry> Entries)
{
    public Gw2ApiKeyEntry? ActiveEntry =>
        Entries.FirstOrDefault(entry => string.Equals(entry.Id, ActiveKeyId, StringComparison.OrdinalIgnoreCase))
        ?? Entries.FirstOrDefault();
}
