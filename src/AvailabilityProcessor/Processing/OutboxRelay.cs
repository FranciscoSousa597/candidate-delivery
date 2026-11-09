using AvailabilityProcessor.Configuration;
using AvailabilityProcessor.Dependencies;
using AvailabilityProcessor.Domain;
using Microsoft.Extensions.Options;

namespace AvailabilityProcessor.Processing;

/// <summary>
/// Publishes the events that are waiting in the outbox: those whose first publication failed, and those that were
/// recorded just before a crash. Scoped, because the publisher is a typed HTTP client.
/// </summary>
public sealed class OutboxRelay(IEventOutbox outbox, IEventPublisher publisher, StockKeyLock keyLock, ILogger<OutboxRelay> logger)
{
    private const int BatchSize = 100;

    /// <summary>Tries to publish the oldest waiting events once.</summary>
    /// <returns>How many events were published.</returns>
    public async Task<int> PublishPendingAsync(CancellationToken ct)
    {
        var published = 0;
        foreach (var pending in await outbox.GetPendingAsync(BatchSize, ct))
        {
            // The processor publishes an event under the lock of its stock key. Taking the same lock here means this
            // relay never publishes an event that the processor is still publishing.
            var key = new StockKey(pending.SellerId, pending.ProductId, pending.SizeId, pending.CountryCode);
            using var keyGuard = await keyLock.AcquireAsync(key, ct);
            if (!await outbox.IsPendingAsync(pending.EventId, ct))
            {
                continue; // published while this relay waited for the key
            }

            try
            {
                await publisher.PublishAsync(pending, ct);
                await outbox.MarkPublishedAsync(pending.EventId, ct);
                published++;
            }
            catch (DependencyException ex) when (ex.IsTransient)
            {
                // The sink is failing. The other events would fail the same way, so they wait for the next run.
                logger.LogWarning("Outbox: publishing {EventId} failed again, the remaining events wait: {Error}", pending.EventId, ex.Message);
                break;
            }
            catch (DependencyException ex)
            {
                logger.LogError("Outbox: the sink rejected {EventId}, which stays in the outbox: {Error}", pending.EventId, ex.Message);
            }
        }

        return published;
    }
}

/// <summary>Runs the <see cref="OutboxRelay"/> at a fixed interval.</summary>
public sealed class OutboxRelayService(
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
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var published = await scope.ServiceProvider.GetRequiredService<OutboxRelay>().PublishPendingAsync(stoppingToken);
                if (published > 0)
                {
                    logger.LogInformation("Outbox: published {Published} waiting event(s).", published);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Outbox relay iteration failed.");
            }
        }
    }
}
