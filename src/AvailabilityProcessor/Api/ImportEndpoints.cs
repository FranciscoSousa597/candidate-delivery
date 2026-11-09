using System.Text;
using AvailabilityProcessor.Configuration;
using AvailabilityProcessor.Imports;
using Microsoft.Extensions.Options;

namespace AvailabilityProcessor.Api;

public static class ImportEndpoints
{
    private static readonly string[] CsvMediaTypes = ["text/csv", "application/csv", "text/plain", "application/octet-stream"];

    private const string HowToUpload =
        "Send the CSV as multipart/form-data field 'file', or as the request body with Content-Type: text/csv.";

    public static IEndpointRouteBuilder MapImportEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/availability/imports", CreateImportAsync);
        app.MapGet("/v1/availability/imports/{jobId:guid}", GetImport);
        return app;
    }

    /// <summary>
    /// Validates the request and the header synchronously, then queues the job and returns 202 right away.
    /// Row-level problems never reject the upload; they are reported in the job status.
    /// </summary>
    private static async Task<IResult> CreateImportAsync(
        HttpRequest request,
        ImportJobStore jobs,
        ImportQueue queue,
        IOptions<BulkImportOptions> options,
        TimeProvider timeProvider,
        CancellationToken ct)
    {
        var upload = await ReadUploadAsync(request, ct);
        if (upload.Problem is not null)
        {
            return upload.Problem;
        }

        ParsedCsv csv;
        try
        {
            csv = StockCsv.Parse(new StringReader(upload.Text!));
        }
        catch (CsvFormatException ex)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid CSV.", detail: ex.Message);
        }

        var job = new ImportJob(Guid.NewGuid(), upload.FileName, csv, options.Value.MaxReportedRowResults, timeProvider.GetUtcNow());
        jobs.Add(job);
        if (!queue.TryEnqueue(job))
        {
            jobs.Remove(job.Id);
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Too many imports in progress.",
                detail: "The import queue is full. Retry later.");
        }

        var statusUrl = $"/v1/availability/imports/{job.Id}";
        return Results.Accepted(statusUrl, new { jobId = job.Id, status = ImportJobStatus.Queued, totalRows = job.TotalRows, statusUrl });
    }

    private static IResult GetImport(Guid jobId, ImportJobStore jobs) =>
        jobs.Find(jobId) is { } job
            ? Results.Ok(job.ToView())
            : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Import job not found.");

    /// <summary>
    /// Accepts multipart/form-data (field "file") or a raw text/csv body. A body that cannot be read is the client's
    /// mistake, so it is rejected here with a 4xx instead of surfacing as an unhandled exception (500).
    /// </summary>
    private static async Task<Upload> ReadUploadAsync(HttpRequest request, CancellationToken ct)
    {
        try
        {
            if (request.HasFormContentType)
            {
                var form = await request.ReadFormAsync(ct);
                var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
                if (file is null)
                {
                    return Upload.Rejected(StatusCodes.Status400BadRequest, "Missing CSV file.", HowToUpload);
                }

                using var fileReader = new StreamReader(file.OpenReadStream(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                return new Upload(await fileReader.ReadToEndAsync(ct), file.FileName, null);
            }

            if (!IsCsvMediaType(request.ContentType))
            {
                return Upload.Rejected(
                    StatusCodes.Status415UnsupportedMediaType,
                    "Unsupported media type.",
                    "Use multipart/form-data (field 'file') or Content-Type: text/csv.");
            }

            using var bodyReader = new StreamReader(request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return new Upload(await bodyReader.ReadToEndAsync(ct), null, null);
        }
        catch (BadHttpRequestException ex)
        {
            // Thrown by the server while the body is read, for example 413 when it exceeds the request size limit.
            return Upload.Rejected(ex.StatusCode, "The request body was rejected.", ex.Message);
        }
        catch (Exception ex) when (request.HasFormContentType && ex is (InvalidDataException or IOException))
        {
            // The form parser gave up: no multipart boundary, truncated content, or a body that is not a form at all.
            return Upload.Rejected(StatusCodes.Status400BadRequest, "Malformed form body.", $"{ex.Message.TrimEnd()} {HowToUpload}");
        }
    }

    private static bool IsCsvMediaType(string? contentType) =>
        contentType is not null && CsvMediaTypes.Any(type => contentType.StartsWith(type, StringComparison.OrdinalIgnoreCase));

    private sealed record Upload(string? Text, string? FileName, IResult? Problem)
    {
        public static Upload Rejected(int statusCode, string title, string detail) =>
            new(null, null, Results.Problem(statusCode: statusCode, title: title, detail: detail));
    }
}
