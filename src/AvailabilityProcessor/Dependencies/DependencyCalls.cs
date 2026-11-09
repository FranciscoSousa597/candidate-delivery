using System.Net;
using System.Text;
using System.Text.Json;
using Polly.RateLimiting;
using Polly.Timeout;

namespace AvailabilityProcessor.Dependencies;

/// <summary>Helpers that turn every way an HTTP call can fail into a <see cref="DependencyException"/>.</summary>
internal static class DependencyCalls
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Buffered JSON content, so the retry pipeline can send the same request again.</summary>
    public static StringContent JsonContent<T>(T value) =>
        new(JsonSerializer.Serialize(value, Json), Encoding.UTF8, "application/json");

    public static bool IsTransient(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)status >= 500;

    public static async Task EnsureSuccessAsync(this HttpResponseMessage response, string dependency, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await ReadSnippetAsync(response, ct);
        throw new DependencyException(
            dependency,
            $"HTTP {(int)response.StatusCode} {response.ReasonPhrase} {body}".TrimEnd(),
            IsTransient(response.StatusCode),
            response.StatusCode);
    }

    public static async Task<T> ExecuteAsync<T>(string dependency, Func<Task<T>> call, CancellationToken ct)
    {
        try
        {
            return await call();
        }
        catch (DependencyException)
        {
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // the caller gave up: not a dependency failure
        }
        catch (JsonException ex)
        {
            throw new DependencyException(dependency, $"Unreadable response body: {ex.Message}", isTransient: false, inner: ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutRejectedException
                                       or RateLimiterRejectedException or OperationCanceledException)
        {
            // Network errors, timeouts (a cancellation the caller did not ask for) and a saturated concurrency limiter.
            throw new DependencyException(dependency, ex.Message, isTransient: true, inner: ex);
        }
    }

    public static Task ExecuteAsync(string dependency, Func<Task> call, CancellationToken ct) =>
        ExecuteAsync(dependency, async () =>
        {
            await call();
            return true;
        }, ct);

    private static async Task<string> ReadSnippetAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            return body.Length <= 300 ? body : body[..300] + "...";
        }
        catch (Exception)
        {
            return string.Empty; // diagnostics only
        }
    }
}
