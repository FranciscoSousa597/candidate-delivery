using AvailabilityProcessor.Contracts;
using AvailabilityProcessor.Domain;

namespace AvailabilityProcessor.Dependencies;

public interface IAvailabilityGateway
{
    /// <exception cref="DependencyException">The write failed after retries.</exception>
    Task UpdateAsync(StockUpdate update, ProductContext context, CancellationToken ct);
}

/// <summary>
/// Writes availability through the Availability API Gateway. PUT is idempotent and carries sourceVersion,
/// so retrying it (or replaying it after an ambiguous timeout) is safe.
/// </summary>
public sealed class HttpAvailabilityGateway(HttpClient http) : IAvailabilityGateway
{
    public const string Name = "availability-api";

    public Task UpdateAsync(StockUpdate update, ProductContext context, CancellationToken ct) =>
        DependencyCalls.ExecuteAsync(Name, async () =>
        {
            var key = update.Key;
            var uri = $"/v1/availability/{Escape(key.SellerId)}/{Escape(key.ProductId)}/{Escape(key.SizeId)}"
                      + $"?countryCode={Escape(key.CountryCode)}";
            var body = new AvailabilityUpdateRequest(update.AvailableQuantity, update.SourceVersion, ProductContextSummary.From(context));

            using var content = DependencyCalls.JsonContent(body);
            using var response = await http.PutAsync(uri, content, ct);
            await response.EnsureSuccessAsync(Name, ct);
        }, ct);

    private static string Escape(string value) => Uri.EscapeDataString(value);
}
