using System.ComponentModel.DataAnnotations;

namespace AvailabilityProcessor.Configuration;

/// <summary>
/// Base URLs of the dependencies. The defaults use 127.0.0.1 rather than localhost: compose publishes WireMock on
/// 127.0.0.1 only, and on Windows "localhost" resolves to ::1 first, which costs about 2 s per new connection.
/// </summary>
public sealed class DependencyOptions
{
    public const string Section = "Dependencies";

    [Required]
    public Uri ProductContextBaseUrl { get; set; } = new("http://127.0.0.1:8080");

    [Required]
    public Uri AvailabilityApiBaseUrl { get; set; } = new("http://127.0.0.1:8080");

    [Required]
    public Uri EventSinkBaseUrl { get; set; } = new("http://127.0.0.1:8080");
}

/// <summary>HTTP resilience settings, applied to each dependency separately.</summary>
public sealed class ResilienceOptions
{
    public const string Section = "Resilience";

    /// <summary>Retries after the first attempt, for transient failures only (408, 429, 5xx, timeouts, network errors).</summary>
    public int MaxRetryAttempts { get; set; } = 3;

    /// <summary>Base delay of the exponential backoff (with jitter). A Retry-After header takes precedence.</summary>
    public TimeSpan BaseRetryDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Upper bound for one logical call, retries included.</summary>
    public TimeSpan TotalTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Maximum in-flight requests per dependency, shared by events, bulk jobs and the outbox relay.</summary>
    public int MaxConcurrentRequests { get; set; } = 16;

    public int MaxQueuedRequests { get; set; } = 1000;
}

public sealed class BulkImportOptions
{
    public const string Section = "BulkImport";

    /// <summary>How many stock keys of one job are processed at the same time.</summary>
    public int MaxDegreeOfParallelism { get; set; } = 8;

    /// <summary>Accepted jobs waiting for the worker. When full, new uploads get 503.</summary>
    public int MaxQueuedJobs { get; set; } = 10;

    /// <summary>Cap on row-level results kept per job (counts are always exact).</summary>
    public int MaxReportedRowResults { get; set; } = 1000;
}

public sealed class OutboxOptions
{
    public const string Section = "Outbox";

    /// <summary>How often the relay retries events whose publication failed.</summary>
    public TimeSpan RelayInterval { get; set; } = TimeSpan.FromSeconds(10);
}
