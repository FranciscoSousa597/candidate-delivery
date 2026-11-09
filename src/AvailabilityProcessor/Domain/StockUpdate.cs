namespace AvailabilityProcessor.Domain;

/// <summary>
/// A validated stock change, independent of how it arrived. Inventory events and CSV rows are both converted
/// to this type and go through the same <see cref="Processing.StockUpdateProcessor"/>.
/// </summary>
/// <param name="CausationId">What caused the update (inbound eventId, or import job and line). Used for tracing only.</param>
public sealed record StockUpdate(StockKey Key, int AvailableQuantity, long SourceVersion, string CausationId);
