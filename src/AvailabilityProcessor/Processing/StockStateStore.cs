using AvailabilityProcessor.Contracts;
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

    /// <summary>
    /// Records the applied version and puts its output event in the outbox, in one transaction: either both are
    /// stored or neither is. A version that is not higher than the stored one never replaces it.
    /// </summary>
    ValueTask RecordAppliedAsync(StockKey key, AppliedStock state, SizeAvailabilityUpdated outputEvent, CancellationToken ct);
}

/// <summary>
/// Output events that are waiting to be published. An event enters the outbox together with its applied version
/// (see <see cref="IStockStateStore.RecordAppliedAsync"/>) and leaves it once the sink has accepted it.
/// </summary>
public interface IEventOutbox
{
    /// <summary>The waiting events, oldest first.</summary>
    ValueTask<IReadOnlyList<SizeAvailabilityUpdated>> GetPendingAsync(int limit, CancellationToken ct);

    ValueTask<bool> IsPendingAsync(string eventId, CancellationToken ct);

    ValueTask MarkPublishedAsync(string eventId, CancellationToken ct);

    ValueTask<int> CountPendingAsync(CancellationToken ct);
}
