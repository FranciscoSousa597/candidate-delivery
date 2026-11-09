using AvailabilityProcessor.Contracts;
using AvailabilityProcessor.Dependencies;
using AvailabilityProcessor.Domain;
using AvailabilityProcessor.Processing;
using AvailabilityProcessor.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace AvailabilityProcessor.Tests;

/// <summary>The shared business flow, with in-memory fakes for the three dependencies.</summary>
public sealed class StockUpdateProcessorTests
{
    private static readonly StockKey Key = Build.Update(version: 0).Key;

    private readonly InMemoryStockStateStore _store = new();
    private readonly RecordingGateway _gateway = new();
    private readonly RecordingPublisher _publisher = new();
    private readonly PendingEventOutbox _outbox = new(NullLogger<PendingEventOutbox>.Instance);
    private readonly StubContexts _contexts = new StubContexts().Add(Build.Context());
    private readonly StockUpdateProcessor _processor;

    public StockUpdateProcessorTests() =>
        _processor = new StockUpdateProcessor(
            _store, new StockKeyLock(), _gateway, _publisher, _outbox, TimeProvider.System, NullLogger<StockUpdateProcessor>.Instance);

    [Fact]
    public async Task New_version_is_written_recorded_and_published_with_canonical_context()
    {
        var update = Build.Update(version: 7, quantity: 3);

        var result = await Process(update);

        Assert.Equal(ProcessingStatus.Applied, result.Status);
        Assert.True(result.EventPublished);
        Assert.Equal(update, Assert.Single(_gateway.Writes));

        var published = Assert.Single(_publisher.Published);
        Assert.Equal(SizeAvailabilityUpdated.Type, published.EventType);
        Assert.Equal(
            ("boutique-42", "style-123", "size-38", "PT", 3, 7L),
            (published.SellerId, published.ProductId, published.SizeId, published.CountryCode, published.AvailableQuantity, published.SourceVersion));
        Assert.Equal("Linen Shirt 123", published.ProductContext.ProductName);
        Assert.Equal("brand-42", published.ProductContext.Brand.Id);
        Assert.Equal("EU 38", published.ProductContext.SizeLabel);
        Assert.Equal(result.OutputEventId, published.EventId);
        Assert.Equal(7, (await _store.GetAsync(Key, default))!.SourceVersion);
    }

    [Fact]
    public async Task Replay_of_the_same_version_with_a_new_eventId_is_a_duplicate_without_side_effects()
    {
        await Process(Build.Update(version: 7) with { CausationId = "msg-0001" });

        var replay = await Process(Build.Update(version: 7) with { CausationId = "msg-0002" });

        Assert.Equal(ProcessingStatus.Skipped, replay.Status);
        Assert.Equal(OutcomeCodes.DuplicateVersion, replay.Code);
        Assert.Single(_gateway.Writes);
        Assert.Single(_publisher.Published);
    }

    [Fact]
    public async Task Older_version_is_stale_and_leaves_the_newer_stock_untouched()
    {
        // Example from the brief: the key is at version 8 / quantity 3, then version 7 / quantity 10 arrives.
        await Process(Build.Update(version: 8, quantity: 3));

        var stale = await Process(Build.Update(version: 7, quantity: 10));

        Assert.Equal(ProcessingStatus.Skipped, stale.Status);
        Assert.Equal(OutcomeCodes.StaleVersion, stale.Code);
        Assert.Equal(8, stale.CurrentVersion);
        var stock = await _store.GetAsync(Key, default);
        Assert.Equal((8L, 3), (stock!.SourceVersion, stock.AvailableQuantity));
        Assert.Single(_gateway.Writes);
        Assert.Single(_publisher.Published);
    }

    [Theory]
    [InlineData("seller-B", "style-123", "size-38", "PT")]    // another seller, same product context
    [InlineData("boutique-42", "style-124", "size-38", "PT")] // another product
    [InlineData("boutique-42", "style-123", "size-40", "PT")] // another size
    [InlineData("boutique-42", "style-123", "size-38", "ES")] // another country
    public async Task Versions_are_only_compared_within_the_same_stock_key(
        string sellerId, string productId, string sizeId, string countryCode)
    {
        var otherKey = new StockKey(sellerId, productId, sizeId, countryCode);
        _contexts.Add(Build.Context(productId, sizeId, countryCode: countryCode));
        await Process(Build.Update(version: 8));

        var result = await Process(Build.Update(version: 7) with { Key = otherKey });

        Assert.Equal(ProcessingStatus.Applied, result.Status);
        Assert.Equal(2, _gateway.Writes.Count);
        Assert.Equal(8, (await _store.GetAsync(Key, default))!.SourceVersion);
        Assert.Equal(7, (await _store.GetAsync(otherKey, default))!.SourceVersion);
    }

    [Theory]
    [InlineData("style-404", "ACTIVE", true, OutcomeCodes.ProductContextNotFound)]
    [InlineData("style-9999", "INACTIVE", true, OutcomeCodes.ProductInactive)]
    [InlineData("style-9998", "ACTIVE", false, OutcomeCodes.CountryNotEnabled)]
    public async Task Unsellable_products_fail_permanently_without_side_effects(
        string productId, string status, bool countryEnabled, string expectedCode)
    {
        if (productId != "style-404")
        {
            _contexts.Add(Build.Context(productId, status: status, countryEnabled: countryEnabled));
        }

        var update = Build.Update(version: 1, productId: productId);

        var result = await Process(update);

        Assert.Equal(ProcessingStatus.Failed, result.Status);
        Assert.Equal(expectedCode, result.Code);
        Assert.False(result.Retryable);
        Assert.Empty(_gateway.Writes);
        Assert.Empty(_publisher.Published);
        Assert.Null(await _store.GetAsync(update.Key, default));
    }

    [Fact]
    public async Task Transient_write_failure_is_retryable_and_does_not_record_the_version()
    {
        _gateway.BeforeWrite = _ => throw new DependencyException("availability-api", "HTTP 503", isTransient: true);

        var failed = await Process(Build.Update(version: 7));

        Assert.Equal(ProcessingStatus.Failed, failed.Status);
        Assert.Equal(OutcomeCodes.AvailabilityWriteFailed, failed.Code);
        Assert.True(failed.Retryable);
        Assert.Empty(_publisher.Published);
        Assert.Null(await _store.GetAsync(Key, default));

        // The broker redelivers the same message later, and now it goes through.
        _gateway.BeforeWrite = null;
        var redelivered = await Process(Build.Update(version: 7));
        Assert.Equal(ProcessingStatus.Applied, redelivered.Status);
    }

    [Fact]
    public async Task Publish_failure_after_a_successful_write_keeps_the_version_and_parks_the_event_in_the_outbox()
    {
        _publisher.FailWith = new DependencyException("event-sink", "HTTP 503", isTransient: true);

        var result = await Process(Build.Update(version: 7));

        Assert.Equal(ProcessingStatus.Applied, result.Status);
        Assert.False(result.EventPublished);
        Assert.Equal(1, _outbox.PendingCount);
        Assert.Equal(7, (await _store.GetAsync(Key, default))!.SourceVersion);

        // A replay must not write the stock again: that version is already applied.
        var replay = await Process(Build.Update(version: 7));
        Assert.Equal(OutcomeCodes.DuplicateVersion, replay.Code);
        Assert.Single(_gateway.Writes);

        // Once the sink recovers, the relay publishes the parked event, with the same deterministic eventId.
        _publisher.FailWith = null;
        Assert.Equal(1, await _outbox.FlushAsync(_publisher, CancellationToken.None));
        Assert.Equal(0, _outbox.PendingCount);
        Assert.Equal(result.OutputEventId, Assert.Single(_publisher.Published).EventId);
    }

    [Fact]
    public async Task Event_arriving_while_a_bulk_row_for_the_same_key_is_in_flight_is_never_overwritten_by_the_older_row()
    {
        // v7 (a bulk row) is slow inside the gateway when v8 (an event) arrives for the same key.
        // Without the per-key lock, v8 would be written first and v7 would then overwrite it downstream.
        var v7Writing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseV7 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _gateway.BeforeWrite = async update =>
        {
            if (update.SourceVersion == 7)
            {
                v7Writing.SetResult();
                await releaseV7.Task;
            }
        };

        var bulkRow = Process(Build.Update(version: 7, quantity: 10));
        await v7Writing.Task;
        var newerEvent = Process(Build.Update(version: 8, quantity: 3));
        await Task.Delay(100); // give v8 every chance to overtake
        Assert.False(newerEvent.IsCompleted, "v8 should wait for the key while v7 is being written.");

        releaseV7.SetResult();
        await Task.WhenAll(bulkRow, newerEvent);

        Assert.Equal(new[] { 7L, 8L }, _gateway.Writes.Select(write => write.SourceVersion));
        var stock = await _store.GetAsync(Key, default);
        Assert.Equal((8L, 3), (stock!.SourceVersion, stock.AvailableQuantity));
    }

    [Fact]
    public void Output_eventId_is_deterministic_per_stock_key_and_version()
    {
        var first = SizeAvailabilityUpdated.DeterministicEventId(Key, 7);

        Assert.Equal(first, SizeAvailabilityUpdated.DeterministicEventId(Key, 7));
        Assert.NotEqual(first, SizeAvailabilityUpdated.DeterministicEventId(Key, 8));
        Assert.NotEqual(first, SizeAvailabilityUpdated.DeterministicEventId(Key with { SellerId = "other" }, 7));
    }

    private Task<ProcessingResult> Process(StockUpdate update) => _processor.ProcessAsync(update, _contexts, CancellationToken.None);
}
