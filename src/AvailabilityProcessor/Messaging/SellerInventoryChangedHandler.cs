using AvailabilityProcessor.Contracts;
using AvailabilityProcessor.Dependencies;
using AvailabilityProcessor.Domain;
using AvailabilityProcessor.Processing;

namespace AvailabilityProcessor.Messaging;

/// <summary>
/// Entry point for inbound <see cref="SellerInventoryChanged"/> messages. POST /_dev/messages calls it today;
/// a production broker consumer would call exactly the same method and turn the result into ack / retry / dead-letter.
/// </summary>
public sealed class SellerInventoryChangedHandler(StockUpdateProcessor processor, ProductContextClient productContexts)
{
    public async Task<ProcessingResult> HandleAsync(SellerInventoryChanged message, CancellationToken ct)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(message.EventId))
        {
            errors.Add("eventId is required.");
        }

        if (!string.Equals(message.EventType, SellerInventoryChanged.Type, StringComparison.Ordinal))
        {
            errors.Add($"eventType must be '{SellerInventoryChanged.Type}'.");
        }

        if (message.SchemaVersion != SellerInventoryChanged.SupportedSchemaVersion)
        {
            errors.Add($"schemaVersion must be {SellerInventoryChanged.SupportedSchemaVersion}.");
        }

        if (message.OccurredAt is null)
        {
            errors.Add("occurredAt is required.");
        }

        var validation = StockUpdateValidator.Validate(
            message.SellerId,
            message.ProductId,
            message.SizeId,
            message.CountryCode,
            IntegerInput.From(message.AvailableQuantity),
            IntegerInput.From(message.SourceVersion),
            causationId: message.EventId?.Trim() ?? string.Empty);

        errors.AddRange(validation.Errors);
        if (errors.Count > 0)
        {
            return ProcessingResult.Invalid(errors);
        }

        // eventId is not used for de-duplication: a replay of the same change may carry a new eventId.
        // The stock key + sourceVersion check inside the processor covers duplicates and replays.
        return await processor.ProcessAsync(validation.Update!, new HttpProductContextSource(productContexts), ct);
    }
}
