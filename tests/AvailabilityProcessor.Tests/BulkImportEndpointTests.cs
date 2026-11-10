using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AvailabilityProcessor.Domain;
using AvailabilityProcessor.Imports;
using AvailabilityProcessor.Tests.Support;

namespace AvailabilityProcessor.Tests;

/// <summary>POST /v1/availability/imports and GET /v1/availability/imports/{jobId} end to end.</summary>
public sealed class BulkImportEndpointTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly TestApp _app = new();
    private readonly HttpClient _client;

    public BulkImportEndpointTests() => _client = _app.CreateClient();

    public void Dispose()
    {
        _client.Dispose();
        _app.Dispose();
    }

    [Fact]
    public async Task Provided_fixture_is_imported_asynchronously_with_one_snapshot_request_and_reimporting_it_is_a_no_op()
    {
        var response = await UploadFixture();

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.True(Guid.TryParse((string?)accepted!["jobId"], out var jobId), "The 202 body must carry the jobId.");
        Assert.Equal($"/v1/availability/imports/{jobId}", (string?)accepted["statusUrl"]);
        Assert.Equal(response.Headers.Location!.ToString(), (string?)accepted["statusUrl"]);

        var job = await WaitForCompletion(response);

        Assert.Equal(jobId, job.JobId);
        Assert.Equal(ImportJobStatus.Completed, job.Status);
        Assert.Equal((10_000, 10_000, 10_000, 0, 0), (job.TotalRows, job.ProcessedRows, job.SucceededRows, job.SkippedRows, job.FailedRows));
        Assert.Empty(job.RowResults);
        Assert.Single(_app.Dependencies.Where("GET", "/v1/product-contexts/snapshot"));
        Assert.Empty(_app.Dependencies.Where("GET", "/v1/products/")); // no per-row lookups
        Assert.Equal(10_000, _app.Dependencies.Where("PUT", "/v1/availability/").Count);
        Assert.Equal(10_000, _app.Dependencies.Where("POST", "/mock-broker/v1/events").Count);

        // Uploading the same file again is safe: every row is a duplicate version, nothing is written twice.
        var again = await WaitForCompletion(await UploadFixture());

        Assert.Equal(ImportJobStatus.Completed, again.Status);
        Assert.Equal((10_000, 0, 0), (again.SkippedRows, again.SucceededRows, again.FailedRows));
        Assert.All(again.RowResults, row => Assert.Equal(OutcomeCodes.DuplicateVersion, row.Code));
        Assert.True(again.RowResultsTruncated); // row results are capped; the counts are always exact
        Assert.Equal(10_000, _app.Dependencies.Where("PUT", "/v1/availability/").Count);
    }

    [Fact]
    public async Task Mixed_upload_reports_partial_failures_per_row_and_never_overwrites_newer_stock()
    {
        // (boutique-42, style-123, size-38, PT) is already at version 8 / quantity 3 because of an inventory event.
        var eventResponse = await _client.PostAsJsonAsync("/_dev/messages", InventoryEvent(version: 8, quantity: 3));
        Assert.Equal(HttpStatusCode.OK, eventResponse.StatusCode);

        const string csv =
            "sellerId,productId,sizeId,countryCode,availableQuantity,sourceVersion\n" +
            "boutique-42,style-123,size-38,PT,10,7\n" +      // line 2: older than the applied v8
            "boutique-42,style-123,size-38,PT,3,8\n" +       // line 3: same version again
            "seller-001,style-1,size-34,PT,5,2\n" +          // line 4: applied
            "seller-002,style-9999,size-38,PT,1,1\n" +       // line 5: inactive product
            "seller-003,style-9998,size-38,PT,1,1\n" +       // line 6: product not enabled in PT
            "seller-004,style-77777,size-38,PT,1,1\n" +      // line 7: not in the snapshot
            "seller-503,style-2,size-36,PT,4,1\n" +          // line 8: Availability API keeps answering 503
            "seller-005,style-3,size-38,PT,not-a-number,1\n"; // line 9: invalid row

        var response = await UploadCsv(csv);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var job = await WaitForCompletion(response);

        Assert.Equal(ImportJobStatus.CompletedWithErrors, job.Status);
        Assert.Equal((8, 8, 1, 2, 5), (job.TotalRows, job.ProcessedRows, job.SucceededRows, job.SkippedRows, job.FailedRows));
        Assert.Equal(
            new[]
            {
                (2, ProcessingStatus.Skipped, OutcomeCodes.StaleVersion, false),
                (3, ProcessingStatus.Skipped, OutcomeCodes.DuplicateVersion, false),
                (5, ProcessingStatus.Failed, OutcomeCodes.ProductInactive, false),
                (6, ProcessingStatus.Failed, OutcomeCodes.CountryNotEnabled, false),
                (7, ProcessingStatus.Failed, OutcomeCodes.ProductContextNotFound, false),
                (8, ProcessingStatus.Failed, OutcomeCodes.AvailabilityWriteFailed, true),
                (9, ProcessingStatus.Failed, OutcomeCodes.InvalidInput, false),
            },
            job.RowResults.Select(row => (row.Line, row.Outcome, row.Code, row.Retryable)));

        var stock = await _client.GetFromJsonAsync<JsonObject>("/_dev/stock/boutique-42/style-123/size-38?countryCode=PT");
        Assert.Equal((8L, 3), ((long)stock!["sourceVersion"]!, (int)stock["availableQuantity"]!));
        Assert.Single(_app.Dependencies.Where("GET", "/v1/product-contexts/snapshot"));

        // Lines 2 and 3 sent nothing downstream: the only write and the only event for that key come from the v8 inventory event.
        var write = Assert.Single(_app.Dependencies.Where("PUT", "/v1/availability/boutique-42/style-123/size-38"));
        Assert.Equal(8, WrittenVersion(write));
        var published = Assert.Single(PublishedEvents(), outputEvent => (string?)outputEvent["sellerId"] == "boutique-42");
        Assert.Equal(8, (long)published["sourceVersion"]!);
    }

    [Theory]
    [InlineData("sellerId,productId,sizeId,countryCode,availableQuantity\nseller-1,style-1,size-34,PT,1")] // no sourceVersion column
    [InlineData("")]                                                                                       // empty file
    public async Task Malformed_uploads_are_rejected_synchronously(string csv)
    {
        var response = await UploadCsv(csv);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_app.Dependencies.Requests);
    }

    [Fact]
    public async Task Multipart_request_without_a_file_is_rejected()
    {
        using var form = new MultipartFormDataContent { { new StringContent("x"), "notAFile" } };

        var response = await _client.PostAsync("/v1/availability/imports", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("multipart/form-data; boundary=XYZ", "this is not a multipart body")] // the boundary never appears
    [InlineData("multipart/form-data", "x")]                                           // no boundary parameter
    public async Task Form_body_that_cannot_be_parsed_is_rejected_with_400(string contentType, string body)
    {
        using var content = new StringContent(body);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);

        var response = await _client.PostAsync("/v1/availability/imports", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_app.Dependencies.Requests);
    }

    [Theory]
    [InlineData("text/csv")]                          // the CSV as the request body, not as a file in a form
    [InlineData("application/json")]
    [InlineData("application/x-www-form-urlencoded")] // what curl --data-binary sends when no Content-Type is given
    public async Task Upload_that_is_not_multipart_is_rejected_with_415(string mediaType)
    {
        using var content = new ByteArrayContent(await File.ReadAllBytesAsync(TestData.BulkCsv));
        content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);

        var response = await _client.PostAsync("/v1/availability/imports", content);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Empty(_app.Dependencies.Requests);
    }

    [Fact]
    public async Task Upload_larger_than_the_request_size_limit_is_rejected_with_413()
    {
        // Kestrel enforces the request size limit (about 30 MB by default) while the body is read. The in-memory
        // test server has no such limit, so this test runs the app on Kestrel with the limit lowered to 1 KB.
        using var app = new TestApp();
        app.UseKestrel(kestrel =>
        {
            kestrel.Listen(IPAddress.Loopback, 0); // a free port, so the test also passes while the service is running
            kestrel.Limits.MaxRequestBodySize = 1024;
        });
        app.StartServer();
        using var client = app.CreateClient();

        var response = await client.PostAsync("/v1/availability/imports", CsvForm(new string('x', 4096)));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Empty(app.Dependencies.Requests);
    }

    [Fact]
    public async Task Import_against_a_failing_dependency_stops_calling_it_and_reports_the_rows_as_retryable()
    {
        // The Availability API answers 503 to every write. Retrying each of the 200 rows would cost 600 calls
        // (three attempts per row). The circuit opens after about 20 failed calls, and the other rows fail at once.
        _app.Dependencies.Override = request =>
            request.Method == "PUT" ? FakeDependencies.Json(HttpStatusCode.ServiceUnavailable, "{}") : null;
        var csv = new StringBuilder("sellerId,productId,sizeId,countryCode,availableQuantity,sourceVersion\n");
        for (var row = 1; row <= 200; row++)
        {
            csv.Append($"seller-{row:D3},style-{row},size-34,PT,1,1\n");
        }

        var job = await WaitForCompletion(await UploadCsv(csv.ToString()));

        Assert.Equal(ImportJobStatus.CompletedWithErrors, job.Status);
        Assert.Equal((200, 0, 200), (job.TotalRows, job.SucceededRows, job.FailedRows));
        Assert.All(job.RowResults, row => Assert.Equal((OutcomeCodes.AvailabilityWriteFailed, true), (row.Code, row.Retryable)));
        Assert.InRange(_app.Dependencies.Where("PUT", "/v1/availability/").Count, 20, 100);
    }

    [Fact]
    public async Task Failed_row_is_listed_even_when_skipped_rows_fill_the_row_result_limit()
    {
        // The limit is 3 row results, and rows are processed one at a time,
        // so the five skipped rows are recorded before the failed one.
        using var app = new TestApp(new Dictionary<string, string?>
        {
            ["BulkImport:MaxReportedRowResults"] = "3",
            ["BulkImport:MaxDegreeOfParallelism"] = "1",
        });
        using var client = app.CreateClient();
        const string header = "sellerId,productId,sizeId,countryCode,availableQuantity,sourceVersion\n";
        const string rows =
            "seller-001,style-1,size-34,PT,5,1\n" + // line 2
            "seller-002,style-2,size-36,PT,5,1\n" + // line 3
            "seller-003,style-3,size-38,PT,5,1\n" + // line 4
            "seller-004,style-4,size-40,PT,5,1\n" + // line 5
            "seller-005,style-5,size-42,PT,5,1\n";  // line 6
        var first = await WaitForCompletion(await UploadCsv(header + rows, client), client);
        Assert.Equal((ImportJobStatus.Completed, 5), (first.Status, first.SucceededRows));

        // The same five rows are now duplicates and are skipped. Line 7 is new and fails: its product is inactive.
        var job = await WaitForCompletion(await UploadCsv(header + rows + "seller-006,style-9999,size-38,PT,1,1\n", client), client);

        Assert.Equal(ImportJobStatus.CompletedWithErrors, job.Status);
        Assert.Equal((6, 5, 1), (job.TotalRows, job.SkippedRows, job.FailedRows));
        Assert.Equal(3, job.RowResults.Count);
        Assert.True(job.RowResultsTruncated);
        var failed = Assert.Single(job.RowResults, row => row.Outcome == ProcessingStatus.Failed);
        Assert.Equal((7, OutcomeCodes.ProductInactive), (failed.Line, failed.Code));
    }

    [Fact]
    public async Task Unknown_job_returns_404()
    {
        var response = await _client.GetAsync($"/v1/availability/imports/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Upload_is_accepted_before_any_row_is_processed()
    {
        // Every dependency call waits, so no row can finish. An upload that waited for its rows would never answer.
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _app.Dependencies.BeforeRespond = _ => release.Task;

        var response = await UploadFixture().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var pending = await GetJob(response);
        Assert.Contains(pending.Status, new[] { ImportJobStatus.Queued, ImportJobStatus.Running });
        Assert.Equal((10_000, 0), (pending.TotalRows, pending.ProcessedRows));
        Assert.Empty(_app.Dependencies.Where("PUT", "/v1/availability/"));

        release.SetResult();
        var job = await WaitForCompletion(response);

        Assert.Equal(ImportJobStatus.Completed, job.Status);
        Assert.Equal(10_000, job.SucceededRows);
    }

    [Fact]
    public async Task Upload_with_several_countries_requests_one_snapshot_per_country()
    {
        // The mock only has a snapshot for PT. It answers 404 for ES, so the ES rows fail on their own.
        const string csv =
            "sellerId,productId,sizeId,countryCode,availableQuantity,sourceVersion\n" +
            "seller-001,style-1,size-34,PT,5,1\n" + // line 2
            "seller-001,style-1,size-34,ES,5,1\n" + // line 3
            "seller-002,style-2,size-36,PT,4,1\n" + // line 4
            "seller-002,style-2,size-36,ES,4,1\n" + // line 5
            "seller-003,style-3,size-38,PT,2,1\n";  // line 6

        var response = await UploadCsv(csv);
        var job = await WaitForCompletion(response);

        Assert.Equal(
            new[] { "ES", "PT" },
            _app.Dependencies.Where("GET", "/v1/product-contexts/snapshot").Select(request => request.CountryCode).Order());
        Assert.Empty(_app.Dependencies.Where("GET", "/v1/products/")); // no per-row lookups
        Assert.Equal(ImportJobStatus.CompletedWithErrors, job.Status);
        Assert.Equal((5, 3, 0, 2), (job.TotalRows, job.SucceededRows, job.SkippedRows, job.FailedRows));
        Assert.Equal(
            new[]
            {
                (3, OutcomeCodes.ProductContextNotFound, false),
                (5, OutcomeCodes.ProductContextNotFound, false),
            },
            job.RowResults.Select(row => (row.Line, row.Code, row.Retryable)));
        Assert.Equal(
            new[] { "PT", "PT", "PT" },
            _app.Dependencies.Where("PUT", "/v1/availability/").Select(request => request.CountryCode));
    }

    [Fact]
    public async Task Newer_event_arriving_while_an_import_is_writing_the_same_key_is_never_overwritten_by_the_older_row()
    {
        // The Availability API is slow on the v7 write of the import row. The v8 event for the same key arrives meanwhile.
        var rowWriteStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRowWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _app.Dependencies.BeforeRespond = async request =>
        {
            if (request.Method == "PUT" && WrittenVersion(request) == 7)
            {
                rowWriteStarted.TrySetResult();
                await releaseRowWrite.Task;
            }
        };

        const string csv =
            "sellerId,productId,sizeId,countryCode,availableQuantity,sourceVersion\n" +
            "boutique-42,style-123,size-38,PT,10,7\n";
        var upload = await UploadCsv(csv);
        await rowWriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(ImportJobStatus.Running, (await GetJob(upload)).Status);

        var newerEvent = _client.PostAsJsonAsync("/_dev/messages", InventoryEvent(version: 8, quantity: 3));
        await Task.Delay(200); // give v8 every chance to overtake
        Assert.False(newerEvent.IsCompleted, "The v8 event should wait for the key while the v7 row is being written.");
        Assert.Single(_app.Dependencies.Where("PUT", "/v1/availability/")); // only the v7 write so far

        releaseRowWrite.SetResult();
        var eventResponse = await newerEvent;
        var job = await WaitForCompletion(upload);

        Assert.Equal(HttpStatusCode.OK, eventResponse.StatusCode);
        Assert.Equal("Applied", (string?)(await eventResponse.Content.ReadFromJsonAsync<JsonObject>())!["status"]);
        Assert.Equal((ImportJobStatus.Completed, 1), (job.Status, job.SucceededRows));
        Assert.Equal(new[] { 7L, 8L }, _app.Dependencies.Where("PUT", "/v1/availability/").Select(WrittenVersion));
        var stock = await _client.GetFromJsonAsync<JsonObject>("/_dev/stock/boutique-42/style-123/size-38?countryCode=PT");
        Assert.Equal((8L, 3), ((long)stock!["sourceVersion"]!, (int)stock["availableQuantity"]!));
    }

    private static object InventoryEvent(long version, int quantity) => new
    {
        eventId = $"msg-{version:D4}",
        eventType = "SellerInventoryChanged",
        schemaVersion = 1,
        occurredAt = "2026-10-20T08:00:00Z",
        sellerId = "boutique-42",
        productId = "style-123",
        sizeId = "size-38",
        countryCode = "PT",
        availableQuantity = quantity,
        sourceVersion = version,
    };

    private static long WrittenVersion(RecordedRequest request) => (long)JsonNode.Parse(request.Body!)!["sourceVersion"]!;

    private List<JsonNode> PublishedEvents() =>
        _app.Dependencies.Where("POST", "/mock-broker/v1/events").Select(request => JsonNode.Parse(request.Body!)!).ToList();

    // The helpers below use the shared application, unless a test passes the client of its own application.
    private async Task<ImportJobView> GetJob(HttpResponseMessage accepted, HttpClient? client = null) =>
        (await (client ?? _client).GetFromJsonAsync<ImportJobView>(accepted.Headers.Location!.ToString(), Json))!;

    private Task<HttpResponseMessage> UploadCsv(string csv, HttpClient? client = null) =>
        (client ?? _client).PostAsync("/v1/availability/imports", CsvForm(csv));

    /// <summary>The CSV as a file in a multipart/form-data body, the only upload format the service accepts.</summary>
    private static MultipartFormDataContent CsvForm(string csv) =>
        new() { { new StringContent(csv, Encoding.UTF8, "text/csv"), "file", "stock.csv" } };

    private async Task<HttpResponseMessage> UploadFixture()
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(await File.ReadAllBytesAsync(TestData.BulkCsv));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(file, "file", "bulk-stock.csv");
        return await _client.PostAsync("/v1/availability/imports", form);
    }

    private async Task<ImportJobView> WaitForCompletion(HttpResponseMessage accepted, HttpClient? client = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            var job = await GetJob(accepted, client);
            if (job.Status is ImportJobStatus.Completed or ImportJobStatus.CompletedWithErrors or ImportJobStatus.Failed)
            {
                return job;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Import is still {job.Status} after 60 s.");
            }

            await Task.Delay(50);
        }
    }
}
