namespace AvailabilityProcessor.Domain;

/// <summary>
/// Identity of one independent stock stream. <c>sourceVersion</c> is only comparable within the same key.
/// </summary>
public readonly record struct StockKey(string SellerId, string ProductId, string SizeId, string CountryCode)
{
    /// <summary>The seller is part of the stock identity, but not of the product context lookup key.</summary>
    public ContextKey ContextKey => new(ProductId, SizeId, CountryCode);

    public override string ToString() => $"{SellerId}/{ProductId}/{SizeId}/{CountryCode}";
}

/// <summary>Lookup key of the Product Context API and of the country snapshot.</summary>
public readonly record struct ContextKey(string ProductId, string SizeId, string CountryCode)
{
    public override string ToString() => $"{ProductId}/{SizeId}/{CountryCode}";
}
