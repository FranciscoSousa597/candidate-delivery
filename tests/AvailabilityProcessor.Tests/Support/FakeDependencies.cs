using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;

namespace AvailabilityProcessor.Tests.Support;

public sealed record RecordedRequest(string Method, string Path, string? CountryCode, string? Body);

/// <summary>
/// In-memory copy of the provided WireMock mappings: same routes, same fixtures and the same deterministic fault inputs.
/// It is plugged in as the primary handler of the app's HttpClients, so the real resilience pipeline
/// (retries, Retry-After, timeouts, concurrency limiter) still runs in the tests.
/// </summary>
public sealed partial class FakeDependencies : HttpMessageHandler
{
    private static readonly HashSet<string> Sizes = ["size-34", "size-36", "size-38", "size-40", "size-42"];

    public ConcurrentQueue<RecordedRequest> Requests { get; } = new();

    /// <summary>Retry-After value sent with 429 responses. The real mock sends 5 seconds; tests keep it short.</summary>
    public string RetryAfterSeconds { get; set; } = "0";

    /// <summary>Per-test override. Return null to fall back to the WireMock-like default behaviour.</summary>
    public Func<RecordedRequest, HttpResponseMessage?>? Override { get; set; }

    /// <summary>
    /// Per-test hook that runs after a request is recorded and before it is answered.
    /// Await a task in it to hold a dependency, as a slow call would.
    /// </summary>
    public Func<RecordedRequest, Task>? BeforeRespond { get; set; }

    public IReadOnlyList<RecordedRequest> Where(string method, string pathPrefix) =>
        Requests.Where(request => request.Method == method && request.Path.StartsWith(pathPrefix, StringComparison.Ordinal)).ToList();

    public HttpResponseMessage TooManyRequests()
    {
        var response = Json(HttpStatusCode.TooManyRequests, """{"error":"injected_failure"}""");
        response.Headers.TryAddWithoutValidation("Retry-After", RetryAfterSeconds);
        return response;
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static string ContextJson(string productId, string sizeId, string status = "ACTIVE", bool countryEnabled = true)
    {
        var number = int.Parse(productId["style-".Length..]);
        var (colorId, colorName) = (number % 3) switch
        {
            1 => ("color-white", "White"),
            2 => ("color-black", "Black"),
            _ => ("color-blue", "Blue"),
        };

        return JsonSerializer.Serialize(new
        {
            productId,
            productName = $"Linen Shirt {number}",
            brand = new { id = "brand-42", name = "Atelier Example" },
            category = new { id = "cat-shirts", name = "Shirts" },
            color = new { id = colorId, name = colorName },
            productStatus = status,
            sizeId,
            sizeLabel = "EU " + sizeId["size-".Length..],
            sizeSystem = "EU",
            countryCode = "PT",
            countryEnabled,
        });
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var recorded = new RecordedRequest(request.Method.Method, uri.AbsolutePath, HttpUtility.ParseQueryString(uri.Query)["countryCode"], body);
        Requests.Enqueue(recorded);

        if (BeforeRespond is { } beforeRespond)
        {
            await beforeRespond(recorded).WaitAsync(cancellationToken);
        }

        if (Override?.Invoke(recorded) is { } overridden)
        {
            return overridden;
        }

        var segments = uri.AbsolutePath.Trim('/').Split('/');
        var isPortugal = recorded.CountryCode == "PT";
        return (recorded.Method, segments) switch
        {
            ("GET", ["v1", "products", var productId, "sizes", var sizeId]) when isPortugal => ProductContext(productId, sizeId),
            ("GET", ["v1", "product-contexts", "snapshot"]) when isPortugal => Snapshot(),
            ("PUT", ["v1", "availability", var sellerId, _, _]) when isPortugal => Availability(sellerId),
            ("POST", ["mock-broker", "v1", "events"]) => EventSink(body ?? string.Empty),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
    }

    // The factory may dispose handler chains when they expire; this fake has nothing to release.
    protected override void Dispose(bool disposing)
    {
    }

    // product-context*.json mappings
    private HttpResponseMessage ProductContext(string productId, string sizeId)
    {
        switch (productId)
        {
            case "style-90001":
                return TooManyRequests();
            case "style-90002":
                return Json(HttpStatusCode.ServiceUnavailable, """{"error":"injected_failure"}""");
        }

        if (!KnownProduct().IsMatch(productId) || !Sizes.Contains(sizeId))
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        var status = productId == "style-9999" ? "INACTIVE" : "ACTIVE";
        var countryEnabled = !(productId == "style-9998" && sizeId == "size-38");
        return Json(HttpStatusCode.OK, ContextJson(productId, sizeId, status, countryEnabled));
    }

    // product-context-snapshot.json: serves the real 50,000-object fixture.
    private static HttpResponseMessage Snapshot()
    {
        var content = new StreamContent(File.OpenRead(TestData.SnapshotPt));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    // availability-api*.json mappings
    private HttpResponseMessage Availability(string sellerId) => sellerId switch
    {
        "seller-429" => TooManyRequests(),
        "seller-503" => Json(HttpStatusCode.ServiceUnavailable, """{"error":"injected_failure"}"""),
        _ => Json(HttpStatusCode.OK, """{"status":"accepted"}"""),
    };

    // event-sink*.json mappings: the fault is selected by the submitted event's eventId.
    private HttpResponseMessage EventSink(string body)
    {
        if (FaultEvent429().IsMatch(body))
        {
            return TooManyRequests();
        }

        return FaultEvent503().IsMatch(body)
            ? Json(HttpStatusCode.ServiceUnavailable, """{"error":"injected_failure"}""")
            : Json(HttpStatusCode.Accepted, """{"status":"recorded"}""");
    }

    [GeneratedRegex("^style-(?:[1-9][0-9]{0,3}|10000)$")]
    private static partial Regex KnownProduct();

    [GeneratedRegex("\"eventId\"\\s*:\\s*\"fault-429\"")]
    private static partial Regex FaultEvent429();

    [GeneratedRegex("\"eventId\"\\s*:\\s*\"fault-503\"")]
    private static partial Regex FaultEvent503();
}
