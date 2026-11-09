using System.Collections.Concurrent;
using AvailabilityProcessor.Contracts;
using AvailabilityProcessor.Dependencies;
using AvailabilityProcessor.Domain;
using AvailabilityProcessor.Processing;

namespace AvailabilityProcessor.Tests.Support;

public sealed class RecordingGateway : IAvailabilityGateway
{
    public ConcurrentQueue<StockUpdate> Writes { get; } = new();

    /// <summary>Runs before a write is recorded: throw to simulate a failure, or await to simulate a slow call.</summary>
    public Func<StockUpdate, Task>? BeforeWrite { get; set; }

    public async Task UpdateAsync(StockUpdate update, ProductContext context, CancellationToken ct)
    {
        if (BeforeWrite is not null)
        {
            await BeforeWrite(update);
        }

        Writes.Enqueue(update);
    }
}

public sealed class RecordingPublisher : IEventPublisher
{
    public ConcurrentQueue<SizeAvailabilityUpdated> Published { get; } = new();

    public Exception? FailWith { get; set; }

    public Task PublishAsync(SizeAvailabilityUpdated outputEvent, CancellationToken ct)
    {
        if (FailWith is not null)
        {
            return Task.FromException(FailWith);
        }

        Published.Enqueue(outputEvent);
        return Task.CompletedTask;
    }
}

public sealed class StubContexts : IProductContextSource
{
    private readonly Dictionary<ContextKey, ProductContext> _contexts = [];

    public StubContexts Add(ProductContext context)
    {
        _contexts[context.Key] = context;
        return this;
    }

    public ValueTask<ProductContext?> FindAsync(ContextKey key, CancellationToken ct) =>
        ValueTask.FromResult(_contexts.GetValueOrDefault(key));
}

public static class Build
{
    public static ProductContext Context(
        string productId = "style-123",
        string sizeId = "size-38",
        string status = "ACTIVE",
        bool countryEnabled = true,
        string countryCode = "PT") =>
        new(
            productId,
            $"Linen Shirt {productId["style-".Length..]}",
            new NamedReference("brand-42", "Atelier Example"),
            new NamedReference("cat-shirts", "Shirts"),
            new NamedReference("color-blue", "Blue"),
            status,
            sizeId,
            "EU " + sizeId["size-".Length..],
            "EU",
            countryCode,
            countryEnabled);

    public static StockUpdate Update(
        long version,
        int quantity = 3,
        string sellerId = "boutique-42",
        string productId = "style-123",
        string sizeId = "size-38") =>
        new(new StockKey(sellerId, productId, sizeId, "PT"), quantity, version, CausationId: $"test-{version}");
}
