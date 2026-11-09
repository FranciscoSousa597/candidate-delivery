using System.Collections.Concurrent;
using AvailabilityProcessor.Contracts;
using AvailabilityProcessor.Dependencies;
using Microsoft.Extensions.Options;
using AvailabilityProcessor.Configuration;

namespace AvailabilityProcessor.Processing;

/// <summary>
/// Events whose availability write succeeded but whose publication failed. The relay retries them until they are
/// published. In-memory for the exercise; in production this is an outbox table written in the same transaction as
/// the applied version, so a crash cannot lose the event.
/// </summary>
public sealed class PendingEventOutbox(ILogger<PendingEventOutbox> logger)
{
    private readonly ConcurrentDictionary<string, PendingEvent> _pending = new();

    public int PendingCount => _pending.Count;

    /// <summary>Idempotent: the same eventId is only queued once.</summary>
    public void Add(SizeAvailabilityUpdated outputEvent) => _pending.TryAdd(outputEvent.EventId, new PendingEvent(outputEvent));

    public IReadOnlyList<SizeAvailabilityUpdated> Snapshot() => _pending.Values.Select(pending => pending.Event).ToArray();

    /// <summary>Tries to publish every pending event once.</summary>
    /// <returns>How many events were published.</returns>
    public async Task<int> FlushAsync(IEventPublisher publisher, CancellationToken ct)
    {
        var published = 0;
        foreach (var (eventId, pending) in _pending)
        {
            try
            {
                await publisher.PublishAsync(pending.Event, ct);
                _pending.TryRemove(eventId, out _);
                published++;
            }
            catch (DependencyException ex)
            {
                pending.Attempts++;
                logger.LogWarning("Outbox: publishing {EventId} failed again (attempt {Attempts}): {Error}", eventId, pending.Attempts, ex.Message);
            }
        }

        return published;
    }

    private sealed class PendingEvent(SizeAvailabilityUpdated outputEvent)
    {
        public SizeAvailabilityUpdated Event { get; } = outputEvent;

        public int Attempts { get; set; }
    }
}

/// <summary>Periodically retries the events waiting in the <see cref="PendingEventOutbox"/>.</summary>
public sealed class OutboxRelayService(
    PendingEventOutbox outbox,
    IServiceScopeFactory scopes,
    IOptions<OutboxOptions> options,
    TimeProvider timeProvider,
    ILogger<OutboxRelayService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.RelayInterval, timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (outbox.PendingCount == 0)
            {
                continue;
            }

            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var publisher = scope.ServiceProvider.GetRequiredService<IEventPublisher>();
                var published = await outbox.FlushAsync(publisher, stoppingToken);
                if (published > 0)
                {
                    logger.LogInformation("Outbox: published {Published} pending event(s); {Remaining} still pending.", published, outbox.PendingCount);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Outbox relay iteration failed.");
            }
        }
    }
}
