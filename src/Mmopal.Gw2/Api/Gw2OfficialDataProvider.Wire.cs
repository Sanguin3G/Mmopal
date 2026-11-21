using System.Text.Json.Serialization;

namespace Mmopal.Gw2;

/// <summary>JSON shapes of the GW2 API responses.</summary>
public sealed partial class Gw2OfficialDataProvider
{
    private sealed record Gw2CurrentTransaction(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("item_id")] int ItemId,
        [property: JsonPropertyName("price")] long Price,
        [property: JsonPropertyName("quantity")] long Quantity,
        [property: JsonPropertyName("created")] DateTimeOffset Created);

    private sealed record Gw2HistoryTransaction(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("item_id")] int ItemId,
        [property: JsonPropertyName("price")] long Price,
        [property: JsonPropertyName("quantity")] long Quantity,
        [property: JsonPropertyName("created")] DateTimeOffset Created,
        [property: JsonPropertyName("purchased")] DateTimeOffset Purchased);

    private sealed record Gw2DeliveryResponse(
        [property: JsonPropertyName("coins")] long Coins,
        [property: JsonPropertyName("items")] Gw2DeliveryEntry[]? Items);

    private sealed record Gw2DeliveryEntry(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("count")] long Count);

    private sealed record Gw2Item(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("rarity")] string? Rarity,
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("vendor_value")] long VendorValue);

    private sealed record Gw2WalletEntry(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("value")] long Value);

    private sealed record Gw2ListingPrice(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("buys")] Gw2PriceLevel? Buys,
        [property: JsonPropertyName("sells")] Gw2PriceLevel? Sells);

    private sealed record Gw2PriceLevel(
        [property: JsonPropertyName("quantity")] long Quantity,
        [property: JsonPropertyName("unit_price")] long UnitPrice);

    private sealed record Gw2TokenInfo(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("permissions")] string[]? Permissions);

    private sealed record Gw2AccountInfo(
        [property: JsonPropertyName("name")] string? Name);

    private sealed record Gw2CharacterEntry(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("bags")] List<Gw2BagEntry?>? Bags);

    private sealed record Gw2BagEntry(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("inventory")] List<Gw2InventorySlot?>? Inventory);

    private sealed record Gw2InventorySlot(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("count")] long Count,
        [property: JsonPropertyName("binding")] string? Binding);
}
