using AvailabilityProcessor.Configuration;
using AvailabilityProcessor.Contracts;
using AvailabilityProcessor.Dependencies;
using AvailabilityProcessor.Domain;
using AvailabilityProcessor.Processing;
using Microsoft.Extensions.Options;

namespace AvailabilityProcessor.Imports;

/// <summary>
/// Runs one import: validate the rows, load one snapshot per country, then send every row through the same
/// <see cref="StockUpdateProcessor"/> used for single events.
/// </summary>
public sealed class ImportJobRunner(
    ProductContextClient productContexts,
    StockUpdateProcessor processor,
    IOptions<BulkImportOptions> options,
    TimeProvider timeProvider,
    ILogger<ImportJobRunner> logger)
{
    /// <summary>Never throws: any problem ends up in the job status, so one bad job cannot stop the worker.</summary>
    public async Task RunAsync(ImportJob job, CancellationToken ct)
    {
        try
        {
            var input = job.TakeInput();
            job.MarkRunning(timeProvider.GetUtcNow());
            logger.LogInformation("Import {JobId} started with {Rows} rows.", job.Id, input.Rows.Count);

            var rows = ValidateRows(job, input);
            var snapshot = await LoadSnapshotsAsync(rows, ct);
            await ProcessRowsAsync(job, rows, snapshot, ct);

            job.Complete(timeProvider.GetUtcNow());
            var view = job.ToView();
            logger.LogInformation(
                "Import {JobId} finished as {Status}: {Succeeded} succeeded, {Skipped} skipped, {Failed} failed.",
                job.Id, view.Status, view.SucceededRows, view.SkippedRows, view.FailedRows);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            job.Fail("Interrupted because the service is shutting down.", timeProvider.GetUtcNow());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Import {JobId} failed unexpectedly.", job.Id);
            job.Fail($"Unexpected error: {ex.Message}", timeProvider.GetUtcNow());
        }
    }

    // Step 1: field validation with the same rules as inventory events. An invalid row fails on its own.
    private static List<ImportRow> ValidateRows(ImportJob job, ParsedCsv input)
    {
        var rows = new List<ImportRow>(input.Rows.Count);
        foreach (var record in input.Rows)
        {
            var mapping = StockCsv.MapRow(record, input.Columns, causationId: $"import:{job.Id}:line:{record.LineNumber}");
            if (mapping.Update is null)
            {
                job.Record(record.LineNumber, ProcessingResult.Invalid(mapping.Errors));
            }
            else
            {
                rows.Add(new ImportRow(record.LineNumber, mapping.Update));
            }
        }

        return rows;
    }

    // Step 2: one snapshot request per distinct country (never one lookup per row), keeping only the contexts the rows need.
    private async Task<SnapshotContexts> LoadSnapshotsAsync(List<ImportRow> rows, CancellationToken ct)
    {
        var contexts = new Dictionary<ContextKey, ProductContext>();
        var unavailable = new Dictionary<string, DependencyException>(StringComparer.Ordinal);

        foreach (var country in rows.GroupBy(row => row.Update.Key.CountryCode))
        {
            var wanted = country.Select(row => row.Update.Key.ContextKey).ToHashSet();
            try
            {
                // null = 404: no snapshot for this country, so its rows will fail with ProductContextNotFound.
                var matches = await productContexts.GetSnapshotAsync(country.Key, wanted, ct);
                if (matches is not null)
                {
                    foreach (var (key, context) in matches)
                    {
                        contexts[key] = context;
                    }
                }

                logger.LogInformation("Snapshot {Country}: found {Matched} of the {Wanted} contexts this import needs.",
                    country.Key, matches?.Count ?? 0, wanted.Count);
            }
            catch (DependencyException ex)
            {
                logger.LogWarning(ex, "Snapshot for {Country} is unavailable.", country.Key);
                unavailable[country.Key] = ex;
            }
        }

        return new SnapshotContexts(new SnapshotProductContextSource(contexts), unavailable);
    }

    // Step 3: the shared business flow. Rows of the same stock key run in file order; different keys run in parallel,
    // bounded by MaxDegreeOfParallelism and by the per-dependency concurrency limiter of the HTTP pipeline.
    private async Task ProcessRowsAsync(ImportJob job, List<ImportRow> rows, SnapshotContexts snapshot, CancellationToken ct)
    {
        var processable = new List<ImportRow>(rows.Count);
        foreach (var row in rows)
        {
            if (snapshot.Unavailable.TryGetValue(row.Update.Key.CountryCode, out var error))
            {
                job.Record(row.Line, ProcessingResult.Failed(
                    OutcomeCodes.ProductContextUnavailable, $"Product context snapshot unavailable: {error.Message}", error.IsTransient));
            }
            else
            {
                processable.Add(row);
            }
        }

        var parallelism = new ParallelOptions { MaxDegreeOfParallelism = options.Value.MaxDegreeOfParallelism, CancellationToken = ct };
        await Parallel.ForEachAsync(processable.GroupBy(row => row.Update.Key), parallelism, async (sameKeyRows, token) =>
        {
            foreach (var row in sameKeyRows)
            {
                job.Record(row.Line, await ProcessRowAsync(row.Update, snapshot.Source, token));
            }
        });
    }

    private async Task<ProcessingResult> ProcessRowAsync(StockUpdate update, IProductContextSource contexts, CancellationToken ct)
    {
        try
        {
            return await processor.ProcessAsync(update, contexts, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Unexpected error while processing {StockKey}.", update.Key);
            return ProcessingResult.Failed(OutcomeCodes.UnexpectedError, ex.Message, retryable: true);
        }
    }

    private sealed record ImportRow(int Line, StockUpdate Update);

    private sealed record SnapshotContexts(IProductContextSource Source, IReadOnlyDictionary<string, DependencyException> Unavailable);
}
