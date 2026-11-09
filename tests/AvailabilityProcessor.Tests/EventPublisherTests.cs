using AvailabilityProcessor.Contracts;
using AvailabilityProcessor.Dependencies;
using AvailabilityProcessor.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace AvailabilityProcessor.Tests;

/// <summary>
/// The sink faults are keyed on the published eventId (fault-429 / fault-503). The service derives eventIds from the
/// stock key and version, so the full flow never produces them; the publisher adapter is tested directly instead.
/// </summary>
public sealed class EventPublisherTests : IDisposable
{
    private readonly TestApp _app = new();

    public void Dispose() => _app.Dispose();

    [Theory]
    [InlineData("fault-429")]
    [InlineData("fault-503")]
    public async Task Sink_fault_eventIds_are_retried_then_surface_as_transient_failures(string eventId)
    {
        using var scope = _app.Services.CreateScope();
        var publisher = scope.ServiceProvider.GetRequiredService<IEventPublisher>();
        var outputEvent = SizeAvailabilityUpdated.Create(Build.Update(version: 7), Build.Context(), DateTimeOffset.UtcNow) with { EventId = eventId };

        var failure = await Assert.ThrowsAsync<DependencyException>(() => publisher.PublishAsync(outputEvent, CancellationToken.None));

        Assert.True(failure.IsTransient);
        Assert.Equal(3, _app.Dependencies.Where("POST", "/mock-broker/v1/events").Count); // 1 attempt + 2 retries
    }

    [Fact]
    public async Task Normal_event_is_accepted_by_the_sink()
    {
        using var scope = _app.Services.CreateScope();
        var publisher = scope.ServiceProvider.GetRequiredService<IEventPublisher>();
        var outputEvent = SizeAvailabilityUpdated.Create(Build.Update(version: 7), Build.Context(), DateTimeOffset.UtcNow);

        await publisher.PublishAsync(outputEvent, CancellationToken.None);

        Assert.Single(_app.Dependencies.Where("POST", "/mock-broker/v1/events"));
    }
}
