using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AvailabilityProcessor.Domain;

namespace AvailabilityProcessor.Contracts;

/// <summary>Normalized output event for downstream consumers.</summary>
public sealed record SizeAvailabilityUpdated(
    string EventId,
    string EventType,
    int SchemaVersion,
    DateTime OccurredAt,
    string SellerId,
    string ProductId,
    string SizeId,
    string CountryCode,
    int AvailableQuantity,
    long SourceVersion,
    ProductContextSummary ProductContext,
    string CausationId)
{
    public const string Type = "SizeAvailabilityUpdated";

    public static SizeAvailabilityUpdated Create(StockUpdate update, ProductContext context, DateTimeOffset occurredAt) => new(
        EventId: DeterministicEventId(update.Key, update.SourceVersion),
        EventType: Type,
        SchemaVersion: 1,
        OccurredAt: occurredAt.UtcDateTime,
        SellerId: update.Key.SellerId,
        ProductId: update.Key.ProductId,
        SizeId: update.Key.SizeId,
        CountryCode: update.Key.CountryCode,
        AvailableQuantity: update.AvailableQuantity,
        SourceVersion: update.SourceVersion,
        ProductContext: ProductContextSummary.From(context),
        CausationId: update.CausationId);

    /// <summary>
    /// Same stock key and version always give the same eventId. If the event is published twice
    /// (outbox retry, lost acknowledgement), consumers can drop the duplicate by eventId.
    /// </summary>
    public static string DeterministicEventId(StockKey key, long sourceVersion)
    {
        var name = string.Join(
            '\n',
            Type,
            key.SellerId,
            key.ProductId,
            key.SizeId,
            key.CountryCode,
            sourceVersion.ToString(CultureInfo.InvariantCulture));

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(name));
        return new Guid(hash.AsSpan(0, 16)).ToString();
    }
}

/// <summary>Body of PUT /v1/availability/{sellerId}/{productId}/{sizeId}.</summary>
public sealed record AvailabilityUpdateRequest(int AvailableQuantity, long SourceVersion, ProductContextSummary ProductContext);
