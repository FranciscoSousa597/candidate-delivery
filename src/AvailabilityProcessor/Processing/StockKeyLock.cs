using System.Collections.Concurrent;
using AvailabilityProcessor.Domain;

namespace AvailabilityProcessor.Processing;

/// <summary>
/// Serialises processing per stock key inside this process, so "check version, write availability, record version"
/// cannot interleave for one key, even when an event and a bulk row for that key are processed at the same time.
/// There is one semaphore per stock key, created on first use and kept for the life of the process, so memory grows
/// with the number of distinct keys. In production, partitioning the broker by stock key replaces this lock.
/// </summary>
public sealed class StockKeyLock
{
    private readonly ConcurrentDictionary<StockKey, SemaphoreSlim> _locks = new();

    public async ValueTask<Releaser> AcquireAsync(StockKey key, CancellationToken ct)
    {
        var keyLock = _locks.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await keyLock.WaitAsync(ct);
        return new Releaser(keyLock);
    }

    public readonly struct Releaser(SemaphoreSlim keyLock) : IDisposable
    {
        public void Dispose() => keyLock.Release();
    }
}
