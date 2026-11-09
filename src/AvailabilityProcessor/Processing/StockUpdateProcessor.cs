using AvailabilityProcessor.Contracts;
using AvailabilityProcessor.Dependencies;
using AvailabilityProcessor.Domain;

namespace AvailabilityProcessor.Processing;

/// <summary>
/// The business flow shared by inventory events and CSV rows:
/// version check, enrichment, availability write, record the applied version, publish the output event.
/// </summary>
public sealed class StockUpdateProcessor(
    IStockStateStore stateStore,
    StockKeyLock keyLock,
    IAvailabilityGateway availabilityGateway,
    IEventPublisher eventPublisher,
    IEventOutbox outbox,
    TimeProvider timeProvider,
    ILogger<StockUpdateProcessor> logger)
{
    public async Task<ProcessingResult> ProcessAsync(StockUpdate update, IProductContextSource contexts, CancellationToken ct)
    {
        // Everything below runs under a per-key lock, so another update for the same stock key (for example an event
        // arriving while a bulk job is running) cannot slip in between the version check and the availability write.
        using var keyGuard = await keyLock.AcquireAsync(update.Key, ct);

        // 1. Version rules first: duplicates and stale updates cost no downstream call.
        var current = await stateStore.GetAsync(update.Key, ct);
        if (current is not null && update.SourceVersion <= current.SourceVersion)
        {
            return SkipAlreadyApplied(update, current);
        }

        // 2. Enrichment with canonical product context.
        ProductContext? context;
        try
        {
            context = await contexts.FindAsync(update.Key.ContextKey, ct);
        }
        catch (DependencyException ex)
        {
            return Fail(update, OutcomeCodes.ProductContextUnavailable, ex.Message, ex.IsTransient);
        }

        switch (context)
        {
            case null:
                return Fail(update, OutcomeCodes.ProductContextNotFound, $"No product context for {update.Key.ContextKey}.", retryable: false);
            case { IsActive: false }:
                return Fail(update, OutcomeCodes.ProductInactive, $"Product {context.ProductId} is {context.ProductStatus}.", retryable: false);
            case { CountryEnabled: false }:
                return Fail(update, OutcomeCodes.CountryNotEnabled, $"Product {context.ProductId} is not enabled in {context.CountryCode}.", retryable: false);
        }

        // 3. Availability write.
        try
        {
            await availabilityGateway.UpdateAsync(update, context, ct);
        }
        catch (DependencyException ex)
        {
            var code = ex.IsTransient ? OutcomeCodes.AvailabilityWriteFailed : OutcomeCodes.AvailabilityWriteRejected;
            return Fail(update, code, ex.Message, ex.IsTransient);
        }

        // 4. The stock really changed downstream, so the version is recorded even if publishing fails below.
        //    Otherwise an older update could pass the version check later and overwrite this one.
        //    The output event goes into the outbox in the same transaction, so a crash here cannot lose it.
        var appliedAt = timeProvider.GetUtcNow();
        var outputEvent = SizeAvailabilityUpdated.Create(update, context, appliedAt);
        await stateStore.RecordAppliedAsync(
            update.Key, new AppliedStock(update.SourceVersion, update.AvailableQuantity, appliedAt), outputEvent, CancellationToken.None);

        // 5. Publish straight away. If it fails, the event stays in the outbox and the relay publishes it later.
        var published = await TryPublishAsync(outputEvent);

        logger.LogDebug("Applied {StockKey} v{Version} (qty {Quantity}); event {EventId} published: {Published}.",
            update.Key, update.SourceVersion, update.AvailableQuantity, outputEvent.EventId, published);

        return ProcessingResult.Applied(outputEvent.EventId, published);
    }

    private ProcessingResult SkipAlreadyApplied(StockUpdate update, AppliedStock current)
    {
        if (update.SourceVersion == current.SourceVersion)
        {
            if (update.AvailableQuantity != current.AvailableQuantity)
            {
                logger.LogWarning(
                    "{StockKey} v{Version} was replayed with quantity {Replayed}, but quantity {Applied} was applied for that version. Keeping the applied value.",
                    update.Key, update.SourceVersion, update.AvailableQuantity, current.AvailableQuantity);
            }

            return ProcessingResult.Skipped(
                OutcomeCodes.DuplicateVersion,
                $"Version {update.SourceVersion} of {update.Key} is already applied.",
                current.SourceVersion);
        }

        logger.LogInformation("Skipping stale {StockKey} v{Version}: v{Current} is already applied.",
            update.Key, update.SourceVersion, current.SourceVersion);

        return ProcessingResult.Skipped(
            OutcomeCodes.StaleVersion,
            $"Version {update.SourceVersion} of {update.Key} is older than the applied version {current.SourceVersion}.",
            current.SourceVersion);
    }

    private ProcessingResult Fail(StockUpdate update, string code, string message, bool retryable)
    {
        logger.LogWarning("Could not apply {StockKey} v{Version}: {Code} (retryable: {Retryable}). {Message}",
            update.Key, update.SourceVersion, code, retryable, message);

        return ProcessingResult.Failed(code, message, retryable);
    }

    private async Task<bool> TryPublishAsync(SizeAvailabilityUpdated outputEvent)
    {
        try
        {
            // Deliberately not tied to the caller's token: the stock already changed, so the event must go out
            // even if the request that triggered it was cancelled.
            await eventPublisher.PublishAsync(outputEvent, CancellationToken.None);
            await outbox.MarkPublishedAsync(outputEvent.EventId, CancellationToken.None);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Publishing {EventId} failed; it stays in the outbox for the relay.", outputEvent.EventId);
            return false;
        }
    }
}
