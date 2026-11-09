using AvailabilityProcessor.Contracts;

namespace AvailabilityProcessor.Dependencies;

public interface IEventPublisher
{
    /// <exception cref="DependencyException">Publication failed after retries.</exception>
    Task PublishAsync(SizeAvailabilityUpdated outputEvent, CancellationToken ct);
}

/// <summary>
/// Local stand-in for the production broker producer: POSTs the event to the recording HTTP sink.
/// Retrying the POST is safe because the eventId is deterministic, so consumers can drop duplicates.
/// </summary>
public sealed class HttpEventPublisher(HttpClient http) : IEventPublisher
{
    public const string Name = "event-sink";

    public Task PublishAsync(SizeAvailabilityUpdated outputEvent, CancellationToken ct) =>
        DependencyCalls.ExecuteAsync(Name, async () =>
        {
            using var content = DependencyCalls.JsonContent(outputEvent);
            using var response = await http.PostAsync("/mock-broker/v1/events", content, ct);
            await response.EnsureSuccessAsync(Name, ct);
        }, ct);
}
