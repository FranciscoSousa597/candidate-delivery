using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AvailabilityProcessor.Contracts;
using AvailabilityProcessor.Dependencies;
using AvailabilityProcessor.Domain;
using AvailabilityProcessor.Processing;
using AvailabilityProcessor.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace AvailabilityProcessor.Tests;

/// <summary>POST /_dev/messages end to end: the real app, the real HTTP pipeline, the WireMock routes faked in memory.</summary>
public sealed class DevMessagesEndpointTests : IDisposable
{
    private readonly TestApp _app = new();
    private readonly HttpClient _client;

    public DevMessagesEndpointTests() => _client = _app.CreateClient();

    public void Dispose()
    {
        _client.Dispose();
        _app.Dispose();
    }

    [Fact]
    public async Task Sample_event_is_enriched_written_and_published()
    {
        var (response, body) = await Send(SampleEvent());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Applied", (string?)body["status"]);

        var lookup = Assert.Single(_app.Dependencies.Where("GET", "/v1/products/"));
        Assert.Equal(("/v1/products/style-123/sizes/size-38", "PT"), (lookup.Path, lookup.CountryCode));

        var write = Assert.Single(_app.Dependencies.Where("PUT", "/v1/availability/"));
        Assert.Equal(("/v1/availability/boutique-42/style-123/size-38", "PT"), (write.Path, write.CountryCode));
        var writeBody = JsonNode.Parse(write.Body!)!;
        Assert.Equal(3, (int)writeBody["availableQuantity"]!);
        Assert.Equal(7, (long)writeBody["sourceVersion"]!);
        Assert.Equal("Linen Shirt 123", (string?)writeBody["productContext"]!["productName"]);
        Assert.Equal("EU 38", (string?)writeBody["productContext"]!["sizeLabel"]);

        var published = Assert.Single(_app.Dependencies.Where("POST", "/mock-broker/v1/events"));
        var outputEvent = JsonSerializer.Deserialize<SizeAvailabilityUpdated>(published.Body!, JsonSerializerOptions.Web)!;
        Assert.Equal(SizeAvailabilityUpdated.Type, outputEvent.EventType);
        Assert.Equal(
            ("boutique-42", "style-123", "size-38", "PT", 3, 7L),
            (outputEvent.SellerId, outputEvent.ProductId, outputEvent.SizeId, outputEvent.CountryCode, outputEvent.AvailableQuantity, outputEvent.SourceVersion));
        Assert.Equal(("brand-42", "Atelier Example"), (outputEvent.ProductContext.Brand.Id, outputEvent.ProductContext.Brand.Name));
        Assert.Equal(("EU 38", "EU"), (outputEvent.ProductContext.SizeLabel, outputEvent.ProductContext.SizeSystem));
        Assert.Equal("msg-0001", outputEvent.CausationId);
        Assert.Equal((string?)body["outputEventId"], outputEvent.EventId);
    }

    [Fact]
    public async Task Replay_with_a_new_eventId_is_acknowledged_without_downstream_calls()
    {
        await Send(SampleEvent());
        var callsAfterFirstDelivery = _app.Dependencies.Requests.Count;

        var replay = SampleEvent();
        replay["eventId"] = "msg-0002";
        var (response, body) = await Send(replay);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(OutcomeCodes.DuplicateVersion, (string?)body["code"]);
        Assert.Equal(callsAfterFirstDelivery, _app.Dependencies.Requests.Count);
    }

    [Fact]
    public async Task Older_event_arriving_late_is_skipped_and_stock_stays_at_the_newer_version()
    {
        await Send(SampleEvent(eventId: "msg-0008", version: 8, quantity: 3));

        var (response, body) = await Send(SampleEvent(eventId: "msg-0007", version: 7, quantity: 10));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(OutcomeCodes.StaleVersion, (string?)body["code"]);
        var stock = await _client.GetFromJsonAsync<JsonObject>("/_dev/stock/boutique-42/style-123/size-38?countryCode=PT");
        Assert.Equal((8L, 3), ((long)stock!["sourceVersion"]!, (int)stock["availableQuantity"]!));
        Assert.Single(_app.Dependencies.Where("PUT", "/v1/availability/"));
    }

    [Fact]
    public async Task Invalid_event_is_rejected_with_every_validation_error_and_no_downstream_call()
    {
        var message = SampleEvent();
        message.Remove("sellerId");
        message["countryCode"] = "Portugal";
        message["availableQuantity"] = -1;
        message["eventType"] = "SomethingElse";

        var (response, body) = await Send(message);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = body["errors"]!.AsArray().Select(error => (string)error!).ToList();
        Assert.Contains("sellerId is required.", errors);
        Assert.Contains(errors, error => error.StartsWith("countryCode must be", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.StartsWith("availableQuantity must be between", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.StartsWith("eventType must be", StringComparison.Ordinal));
        Assert.Empty(_app.Dependencies.Requests);
    }

    [Fact]
    public async Task Malformed_json_is_rejected()
    {
        var response = await _client.PostAsync("/_dev/messages", new StringContent("{ not json", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("style-9999", OutcomeCodes.ProductInactive)]
    [InlineData("style-9998", OutcomeCodes.CountryNotEnabled)]
    [InlineData("style-77777", OutcomeCodes.ProductContextNotFound)]
    public async Task Unsellable_products_return_422_and_are_not_written(string productId, string expectedCode)
    {
        var message = SampleEvent();
        message["productId"] = productId;

        var (response, body) = await Send(message);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(expectedCode, (string?)body["code"]);
        Assert.Empty(_app.Dependencies.Where("PUT", "/v1/availability/"));
        Assert.Empty(_app.Dependencies.Where("POST", "/mock-broker/v1/events"));
    }

    [Theory]
    [InlineData("productId", "style-90001", "GET", "/v1/products/")]   // Product Context: 429 with Retry-After
    [InlineData("productId", "style-90002", "GET", "/v1/products/")]   // Product Context: 503
    [InlineData("sellerId", "seller-429", "PUT", "/v1/availability/")] // Availability API: 429 with Retry-After
    [InlineData("sellerId", "seller-503", "PUT", "/v1/availability/")] // Availability API: 503
    public async Task Persistent_dependency_faults_are_retried_then_reported_as_retryable(string field, string value, string method, string path)
    {
        var message = SampleEvent();
        message[field] = value;

        var (response, body) = await Send(message);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True((bool)body["retryable"]!);
        Assert.Equal(3, _app.Dependencies.Where(method, path).Count); // 1 attempt + 2 retries (test settings)
        Assert.Empty(_app.Dependencies.Where("POST", "/mock-broker/v1/events"));

        // Nothing was recorded, so a redelivery of this message can still be applied.
        var stock = await _client.GetAsync($"/_dev/stock/{(string?)message["sellerId"]}/{(string?)message["productId"]}/size-38?countryCode=PT");
        Assert.Equal(HttpStatusCode.NotFound, stock.StatusCode);
    }

    [Fact]
    public async Task Retry_After_is_honoured_and_a_recovered_dependency_succeeds()
    {
        _app.Dependencies.RetryAfterSeconds = "1";
        var lookups = 0;
        _app.Dependencies.Override = request =>
            request.Path.StartsWith("/v1/products/", StringComparison.Ordinal) && Interlocked.Increment(ref lookups) == 1
                ? _app.Dependencies.TooManyRequests()
                : null;

        var stopwatch = Stopwatch.StartNew();
        var (response, _) = await Send(SampleEvent());
        stopwatch.Stop();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, _app.Dependencies.Where("GET", "/v1/products/").Count);
        Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(900), $"Expected to wait for Retry-After (1 s), waited {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task Publish_failure_after_the_write_is_acknowledged_and_the_event_is_relayed_later()
    {
        _app.Dependencies.Override = request =>
            request.Method == "POST" ? FakeDependencies.Json(HttpStatusCode.ServiceUnavailable, "{}") : null;

        var (response, body) = await Send(SampleEvent());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode); // the stock changed, so the message must not be redelivered
        Assert.False((bool)body["eventPublished"]!);
        Assert.Single(_app.Dependencies.Where("PUT", "/v1/availability/"));
        var outbox = await _client.GetFromJsonAsync<JsonObject>("/_dev/outbox");
        Assert.Equal(1, (int)outbox!["pending"]!);

        // The sink recovers and the relay publishes the parked event.
        _app.Dependencies.Override = null;
        using var scope = _app.Services.CreateScope();
        var relayed = await _app.Services.GetRequiredService<PendingEventOutbox>()
            .FlushAsync(scope.ServiceProvider.GetRequiredService<IEventPublisher>(), CancellationToken.None);

        Assert.Equal(1, relayed);
        var attempts = _app.Dependencies.Where("POST", "/mock-broker/v1/events");
        Assert.Equal(4, attempts.Count); // 3 failed attempts inline, 1 successful relay
        Assert.Single(attempts.Select(attempt => (string?)JsonNode.Parse(attempt.Body!)!["eventId"]).Distinct());
    }

    [Fact]
    public async Task Circuit_opens_after_repeated_failures_and_closes_again_when_the_dependency_recovers()
    {
        // A low threshold, so that six failed calls open the circuit: two events with three attempts each.
        using var app = new TestApp(new Dictionary<string, string?>
        {
            ["Resilience:CircuitBreaker:MinimumThroughput"] = "6",
            ["Resilience:CircuitBreaker:BreakDuration"] = "00:00:00.500",
        });
        using var client = app.CreateClient();
        app.Dependencies.Override = request =>
            request.Method == "PUT" ? FakeDependencies.Json(HttpStatusCode.ServiceUnavailable, "{}") : null;

        foreach (var seller in new[] { "seller-a", "seller-b" })
        {
            var (failed, _) = await Send(EventFor(seller), client);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        }

        Assert.Equal(6, app.Dependencies.Where("PUT", "/v1/availability/").Count);

        // The circuit is open: the next event fails at once, and its write is not sent.
        var (rejected, body) = await Send(EventFor("seller-c"), client);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, rejected.StatusCode);
        Assert.True((bool)body["retryable"]!);
        Assert.Contains("Circuit open", (string?)body["message"]);
        Assert.Equal(6, app.Dependencies.Where("PUT", "/v1/availability/").Count);

        // The dependency recovers. After the break, one trial call goes through and closes the circuit.
        app.Dependencies.Override = null;
        await Task.Delay(TimeSpan.FromMilliseconds(700));
        var (recovered, _) = await Send(EventFor("seller-c"), client);

        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.Equal(7, app.Dependencies.Where("PUT", "/v1/availability/").Count);

        static JsonObject EventFor(string sellerId)
        {
            var message = SampleEvent();
            message["sellerId"] = sellerId;
            return message;
        }
    }

    private static JsonObject SampleEvent(string? eventId = null, long? version = null, int? quantity = null)
    {
        var message = JsonNode.Parse(File.ReadAllText(TestData.SampleEvent))!.AsObject();
        if (eventId is not null)
        {
            message["eventId"] = eventId;
        }

        if (version is not null)
        {
            message["sourceVersion"] = version;
        }

        if (quantity is not null)
        {
            message["availableQuantity"] = quantity;
        }

        return message;
    }

    private async Task<(HttpResponseMessage Response, JsonObject Body)> Send(JsonObject message, HttpClient? client = null)
    {
        var response = await (client ?? _client).PostAsync("/_dev/messages", new StringContent(message.ToJsonString(), Encoding.UTF8, "application/json"));
        var body = await response.Content.ReadFromJsonAsync<JsonObject>();
        return (response, body!);
    }
}
