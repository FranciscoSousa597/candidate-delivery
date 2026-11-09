using AvailabilityProcessor.Contracts;
using AvailabilityProcessor.Dependencies;
using AvailabilityProcessor.Domain;

namespace AvailabilityProcessor.Processing;

/// <summary>
/// Where the processor gets canonical product context from. This is the only step that differs between the two
/// input paths: single events use the HTTP lookup, bulk imports use contexts pre-loaded from the country snapshot.
/// </summary>
public interface IProductContextSource
{
    /// <returns>The context, or null when it does not exist.</returns>
    /// <exception cref="DependencyException">The source could not be reached.</exception>
    ValueTask<ProductContext?> FindAsync(ContextKey key, CancellationToken ct);
}

public sealed class HttpProductContextSource(ProductContextClient client) : IProductContextSource
{
    public async ValueTask<ProductContext?> FindAsync(ContextKey key, CancellationToken ct) => await client.GetAsync(key, ct);
}

public sealed class SnapshotProductContextSource(IReadOnlyDictionary<ContextKey, ProductContext> contexts) : IProductContextSource
{
    public ValueTask<ProductContext?> FindAsync(ContextKey key, CancellationToken ct) =>
        ValueTask.FromResult(contexts.GetValueOrDefault(key));
}
