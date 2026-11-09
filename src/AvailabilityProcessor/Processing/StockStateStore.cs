using System.Collections.Concurrent;
using AvailabilityProcessor.Domain;

namespace AvailabilityProcessor.Processing;

/// <summary>The latest successfully applied version (and quantity) of one stock key.</summary>
public sealed record AppliedStock(long SourceVersion, int AvailableQuantity, DateTimeOffset AppliedAt);

/// <summary>
/// Latest successfully applied version per stock key: the source of truth for stale and duplicate checks,
/// shared by the event path and the bulk path.
/// </summary>
public interface IStockStateStore
{
    ValueTask<AppliedStock?> GetAsync(StockKey key, CancellationToken ct);

    ValueTask MarkAppliedAsync(StockKey key, AppliedStock state, CancellationToken ct);
}

/// <summary>
/// In-memory implementation for the exercise. In production this would be a shared store (e.g. a database row per key
/// updated with "WHERE version &lt; @new") so that several service instances agree on the applied version.
/// </summary>
public sealed class InMemoryStockStateStore : IStockStateStore
{
    private readonly ConcurrentDictionary<StockKey, AppliedStock> _state = new();

    public ValueTask<AppliedStock?> GetAsync(StockKey key, CancellationToken ct) =>
        ValueTask.FromResult<AppliedStock?>(_state.TryGetValue(key, out var state) ? state : null);

    public ValueTask MarkAppliedAsync(StockKey key, AppliedStock state, CancellationToken ct)
    {
        // Monotonic even without the caller's lock: a lower version can never replace a higher one.
        _state.AddOrUpdate(key, state, (_, current) => state.SourceVersion > current.SourceVersion ? state : current);
        return ValueTask.CompletedTask;
    }
}
