using System.Net;

namespace AvailabilityProcessor.Dependencies;

/// <summary>A call to a dependency failed after the resilience pipeline (retries, timeouts) gave up.</summary>
public sealed class DependencyException(
    string dependency,
    string message,
    bool isTransient,
    HttpStatusCode? statusCode = null,
    Exception? inner = null)
    : Exception($"{dependency}: {message}", inner)
{
    public string Dependency { get; } = dependency;

    /// <summary>True when trying again later may succeed (408, 429, 5xx, timeouts, network errors).</summary>
    public bool IsTransient { get; } = isTransient;

    public HttpStatusCode? StatusCode { get; } = statusCode;
}
