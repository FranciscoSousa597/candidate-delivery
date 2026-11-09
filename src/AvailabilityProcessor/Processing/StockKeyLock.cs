using AvailabilityProcessor.Domain;

namespace AvailabilityProcessor.Processing;

/// <summary>
/// Serialises processing per stock key inside this process, so "check version, write availability, record version"
/// cannot interleave for one key, even when an event and a bulk row for that key are processed at the same time.
/// Lock striping keeps memory bounded: unrelated keys may share a stripe, which only costs a little parallelism.
/// </summary>
public sealed class StockKeyLock
{
    private readonly SemaphoreSlim[] _stripes;

    public StockKeyLock(int stripes = 1024)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stripes);
        _stripes = new SemaphoreSlim[stripes];
        for (var i = 0; i < stripes; i++)
        {
            _stripes[i] = new SemaphoreSlim(1, 1);
        }
    }

    public async ValueTask<Releaser> AcquireAsync(StockKey key, CancellationToken ct)
    {
        var stripe = _stripes[(int)((uint)key.GetHashCode() % (uint)_stripes.Length)];
        await stripe.WaitAsync(ct);
        return new Releaser(stripe);
    }

    public readonly struct Releaser(SemaphoreSlim stripe) : IDisposable
    {
        public void Dispose() => stripe.Release();
    }
}
