using AvailabilityProcessor.Contracts;
using AvailabilityProcessor.Domain;
using AvailabilityProcessor.Persistence;
using AvailabilityProcessor.Processing;
using AvailabilityProcessor.Tests.Support;

namespace AvailabilityProcessor.Tests;

/// <summary>The SQL behind the applied versions and the outbox, against a SQLite database kept in memory.</summary>
public sealed class SqliteStockStoreTests : IDisposable
{
    private static readonly StockKey Key = Build.Update(version: 0).Key;

    private readonly SqliteStockStore _store = new("Data Source=:memory:");

    public void Dispose() => _store.Dispose();

    [Fact]
    public async Task Applied_version_and_its_event_are_stored_together_and_the_event_leaves_once_published()
    {
        var outputEvent = EventFor(version: 7);

        await _store.RecordAppliedAsync(Key, Applied(version: 7, quantity: 3), outputEvent, default);

        var stock = await _store.GetAsync(Key, default);
        Assert.Equal((7L, 3), (stock!.SourceVersion, stock.AvailableQuantity));
        var pending = Assert.Single(await _store.GetPendingAsync(limit: 10, default));
        Assert.Equal((outputEvent.EventId, 7L, "Linen Shirt 123"), (pending.EventId, pending.SourceVersion, pending.ProductContext.ProductName));
        Assert.True(await _store.IsPendingAsync(outputEvent.EventId, default));

        await _store.MarkPublishedAsync(outputEvent.EventId, default);

        Assert.Equal(0, await _store.CountPendingAsync(default));
        Assert.False(await _store.IsPendingAsync(outputEvent.EventId, default));
        Assert.Equal(7, (await _store.GetAsync(Key, default))!.SourceVersion); // the version stays
    }

    [Fact]
    public async Task Version_that_is_not_higher_never_replaces_the_stored_one()
    {
        await _store.RecordAppliedAsync(Key, Applied(version: 8, quantity: 3), EventFor(version: 8), default);

        await _store.RecordAppliedAsync(Key, Applied(version: 7, quantity: 10), EventFor(version: 7), default);
        await _store.RecordAppliedAsync(Key, Applied(version: 8, quantity: 99), EventFor(version: 8), default);

        var stock = await _store.GetAsync(Key, default);
        Assert.Equal((8L, 3), (stock!.SourceVersion, stock.AvailableQuantity));
    }

    [Fact]
    public async Task Unknown_key_has_no_applied_version()
    {
        Assert.Null(await _store.GetAsync(Key, default));
        Assert.Empty(await _store.GetPendingAsync(limit: 10, default));
    }

    private static AppliedStock Applied(long version, int quantity) => new(version, quantity, DateTimeOffset.UtcNow);

    private static SizeAvailabilityUpdated EventFor(long version) =>
        SizeAvailabilityUpdated.Create(Build.Update(version), Build.Context(), DateTimeOffset.UtcNow);
}
