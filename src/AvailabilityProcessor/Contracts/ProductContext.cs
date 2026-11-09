using System.Text.Json.Serialization;
using AvailabilityProcessor.Domain;

namespace AvailabilityProcessor.Contracts;

/// <summary>Canonical product, size and country data returned by the Product Context API (single lookup and snapshot).</summary>
public sealed record ProductContext(
    string ProductId,
    string ProductName,
    NamedReference Brand,
    NamedReference Category,
    NamedReference Color,
    string ProductStatus,
    string SizeId,
    string SizeLabel,
    string SizeSystem,
    string CountryCode,
    bool CountryEnabled)
{
    public const string ActiveStatus = "ACTIVE";

    [JsonIgnore]
    public ContextKey Key => new(ProductId, SizeId, CountryCode);

    [JsonIgnore]
    public bool IsActive => string.Equals(ProductStatus, ActiveStatus, StringComparison.OrdinalIgnoreCase);
}

public sealed record NamedReference(string Id, string Name);

/// <summary>The canonical fields copied into the availability write and the output event.</summary>
public sealed record ProductContextSummary(
    string ProductName,
    NamedReference Brand,
    NamedReference Category,
    NamedReference Color,
    string SizeLabel,
    string SizeSystem)
{
    public static ProductContextSummary From(ProductContext context) => new(
        context.ProductName,
        context.Brand,
        context.Category,
        context.Color,
        context.SizeLabel,
        context.SizeSystem);
}
