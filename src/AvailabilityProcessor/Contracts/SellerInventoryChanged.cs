namespace AvailabilityProcessor.Contracts;

/// <summary>
/// Inbound event contract. Every field is nullable so that a missing field becomes a clear validation error
/// instead of a silent default value.
/// </summary>
public sealed record SellerInventoryChanged(
    string? EventId,
    string? EventType,
    int? SchemaVersion,
    DateTimeOffset? OccurredAt,
    string? SellerId,
    string? ProductId,
    string? SizeId,
    string? CountryCode,
    long? AvailableQuantity,
    long? SourceVersion)
{
    public const string Type = "SellerInventoryChanged";
    public const int SupportedSchemaVersion = 1;
}
