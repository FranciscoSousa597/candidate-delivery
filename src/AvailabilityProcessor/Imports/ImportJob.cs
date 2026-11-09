using System.Collections.Concurrent;
using AvailabilityProcessor.Domain;

namespace AvailabilityProcessor.Imports;

public enum ImportJobStatus
{
    Queued,
    Running,

    /// <summary>Every row was applied or skipped.</summary>
    Completed,

    /// <summary>Finished, but at least one row failed (partial failure). See the row results.</summary>
    CompletedWithErrors,

    /// <summary>The job itself could not run to the end (e.g. the service shut down).</summary>
    Failed,
}

/// <summary>A row that was not applied (failed or skipped). <see cref="Line"/> is the physical line in the file (header = 1).</summary>
public sealed record RowResult(int Line, ProcessingStatus Outcome, string Code, string Message, bool Retryable);

public sealed record ImportJobView(
    Guid JobId,
    ImportJobStatus Status,
    string? FileName,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    int TotalRows,
    int ProcessedRows,
    int SucceededRows,
    int SkippedRows,
    int FailedRows,
    IReadOnlyList<RowResult> RowResults,
    bool RowResultsTruncated,
    string? Error);

/// <summary>State of one asynchronous import. Thread-safe: rows are recorded by parallel workers.</summary>
public sealed class ImportJob(Guid id, string? fileName, ParsedCsv input, int maxRowResults, DateTimeOffset createdAt)
{
    private readonly Lock _gate = new();
    private readonly List<RowResult> _rowResults = [];
    private ParsedCsv? _input = input;
    private int _succeeded;
    private int _skipped;
    private int _failed;
    private bool _rowResultsTruncated;
    private ImportJobStatus _status = ImportJobStatus.Queued;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _completedAt;
    private string? _error;

    public Guid Id { get; } = id;

    public int TotalRows { get; } = input.Rows.Count;

    /// <summary>Hands the parsed rows to the runner and drops the job's reference, so they can be collected afterwards.</summary>
    internal ParsedCsv TakeInput()
    {
        lock (_gate)
        {
            var taken = _input ?? throw new InvalidOperationException($"Import {Id} was already started.");
            _input = null;
            return taken;
        }
    }

    internal void MarkRunning(DateTimeOffset now)
    {
        lock (_gate)
        {
            _status = ImportJobStatus.Running;
            _startedAt = now;
        }
    }

    internal void Record(int line, ProcessingResult result)
    {
        switch (result.Status)
        {
            case ProcessingStatus.Applied:
                Interlocked.Increment(ref _succeeded);
                return; // applied rows are only counted, which keeps the status response small
            case ProcessingStatus.Skipped:
                Interlocked.Increment(ref _skipped);
                break;
            default:
                Interlocked.Increment(ref _failed);
                break;
        }

        lock (_gate)
        {
            if (_rowResults.Count < maxRowResults)
            {
                _rowResults.Add(new RowResult(line, result.Status, result.Code, result.Message, result.Retryable));
            }
            else
            {
                _rowResultsTruncated = true;
            }
        }
    }

    internal void Complete(DateTimeOffset now)
    {
        lock (_gate)
        {
            _status = Volatile.Read(ref _failed) > 0 ? ImportJobStatus.CompletedWithErrors : ImportJobStatus.Completed;
            _completedAt = now;
        }
    }

    internal void Fail(string error, DateTimeOffset now)
    {
        lock (_gate)
        {
            _status = ImportJobStatus.Failed;
            _error = error;
            _completedAt = now;
        }
    }

    public ImportJobView ToView()
    {
        lock (_gate)
        {
            var succeeded = Volatile.Read(ref _succeeded);
            var skipped = Volatile.Read(ref _skipped);
            var failed = Volatile.Read(ref _failed);

            return new ImportJobView(
                Id,
                _status,
                fileName,
                createdAt,
                _startedAt,
                _completedAt,
                TotalRows,
                ProcessedRows: succeeded + skipped + failed,
                SucceededRows: succeeded,
                SkippedRows: skipped,
                FailedRows: failed,
                _rowResults.OrderBy(row => row.Line).ToArray(),
                _rowResultsTruncated,
                _error);
        }
    }
}

/// <summary>In-memory job registry. In production: a table (jobs + row results) with a retention period.</summary>
public sealed class ImportJobStore
{
    private readonly ConcurrentDictionary<Guid, ImportJob> _jobs = new();

    public void Add(ImportJob job) => _jobs[job.Id] = job;

    public void Remove(Guid id) => _jobs.TryRemove(id, out _);

    public ImportJob? Find(Guid id) => _jobs.GetValueOrDefault(id);
}
