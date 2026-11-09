using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AvailabilityProcessor.Contracts;
using AvailabilityProcessor.Domain;

namespace AvailabilityProcessor.Dependencies;

/// <summary>Client for the Product Context API: single lookup for events, country snapshot for bulk imports.</summary>
public sealed class ProductContextClient(HttpClient http)
{
    public const string Name = "product-context-api";

    /// <returns>The context, or null when the API answers 404 (unknown product, size or country).</returns>
    public Task<ProductContext?> GetAsync(ContextKey key, CancellationToken ct) =>
        DependencyCalls.ExecuteAsync<ProductContext?>(Name, async () =>
        {
            var uri = $"/v1/products/{Escape(key.ProductId)}/sizes/{Escape(key.SizeId)}?countryCode={Escape(key.CountryCode)}";
            using var response = await http.GetAsync(uri, ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            await response.EnsureSuccessAsync(Name, ct);
            return await response.Content.ReadFromJsonAsync<ProductContext>(DependencyCalls.Json, ct);
        }, ct);

    /// <summary>
    /// Streams the country snapshot and keeps only the contexts listed in <paramref name="wanted"/>.
    /// Time is O(m) for m snapshot objects and memory is O(|wanted|): the full snapshot is never held in memory.
    /// </summary>
    /// <returns>The matched contexts, or null when the API has no snapshot for that country (404).</returns>
    public Task<Dictionary<ContextKey, ProductContext>?> GetSnapshotAsync(
        string countryCode,
        IReadOnlySet<ContextKey> wanted,
        CancellationToken ct) =>
        DependencyCalls.ExecuteAsync<Dictionary<ContextKey, ProductContext>?>(Name, async () =>
        {
            var uri = $"/v1/product-contexts/snapshot?countryCode={Escape(countryCode)}";
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            await response.EnsureSuccessAsync(Name, ct);

            await using var body = await response.Content.ReadAsStreamAsync(ct);
            var matches = new Dictionary<ContextKey, ProductContext>(wanted.Count);
            await foreach (var context in JsonSerializer.DeserializeAsyncEnumerable<ProductContext>(body, DependencyCalls.Json, ct))
            {
                if (context is not null && wanted.Contains(context.Key))
                {
                    matches.TryAdd(context.Key, context);
                }
            }

            return matches;
        }, ct);

    private static string Escape(string value) => Uri.EscapeDataString(value);
}
