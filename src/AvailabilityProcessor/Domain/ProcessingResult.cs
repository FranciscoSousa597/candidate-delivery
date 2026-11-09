namespace AvailabilityProcessor.Domain;

public enum ProcessingStatus
{
    /// <summary>Availability written and output event published (or queued in the outbox for retry).</summary>
    Applied,

    /// <summary>Nothing to do: the version was already applied (duplicate) or a newer one was (stale).</summary>
    Skipped,

    /// <summary>Not applied. <see cref="ProcessingResult.Retryable"/> says whether trying again later can help.</summary>
    Failed,
}

public static class OutcomeCodes
{
    public const string Applied = "Applied";
    public const string DuplicateVersion = "DuplicateVersion";
    public const string StaleVersion = "StaleVersion";
    public const string InvalidInput = "InvalidInput";
    public const string ProductContextNotFound = "ProductContextNotFound";
    public const string ProductInactive = "ProductInactive";
    public const string CountryNotEnabled = "CountryNotEnabled";
    public const string ProductContextUnavailable = "ProductContextUnavailable";
    public const string AvailabilityWriteFailed = "AvailabilityWriteFailed";
    public const string AvailabilityWriteRejected = "AvailabilityWriteRejected";
    public const string UnexpectedError = "UnexpectedError";
}

/// <summary>Outcome of processing one stock update (event or CSV row).</summary>
public sealed record ProcessingResult(
    ProcessingStatus Status,
    string Code,
    string Message,
    bool Retryable = false,
    long? CurrentVersion = null,
    string? OutputEventId = null,
    bool? EventPublished = null,
    IReadOnlyList<string>? Errors = null)
{
    public static ProcessingResult Applied(string outputEventId, bool eventPublished) => new(
        ProcessingStatus.Applied,
        OutcomeCodes.Applied,
        eventPublished
            ? "Availability updated and SizeAvailabilityUpdated published."
            : "Availability updated. Publishing the event failed, so it was queued in the outbox for retry.",
        OutputEventId: outputEventId,
        EventPublished: eventPublished);

    public static ProcessingResult Skipped(string code, string message, long currentVersion) =>
        new(ProcessingStatus.Skipped, code, message, CurrentVersion: currentVersion);

    public static ProcessingResult Failed(string code, string message, bool retryable) =>
        new(ProcessingStatus.Failed, code, message, retryable);

    public static ProcessingResult Invalid(IReadOnlyList<string> errors) =>
        new(ProcessingStatus.Failed, OutcomeCodes.InvalidInput, string.Join(" ", errors), Errors: errors);
}
