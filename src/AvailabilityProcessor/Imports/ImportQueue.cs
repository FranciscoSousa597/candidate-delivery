using System.Threading.Channels;
using AvailabilityProcessor.Configuration;
using Microsoft.Extensions.Options;

namespace AvailabilityProcessor.Imports;

/// <summary>Bounded queue of accepted imports. When it is full, uploads are refused (503) instead of piling up in memory.</summary>
public sealed class ImportQueue(IOptions<BulkImportOptions> options)
{
    private readonly Channel<ImportJob> _channel = Channel.CreateBounded<ImportJob>(
        new BoundedChannelOptions(options.Value.MaxQueuedJobs)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

    public bool TryEnqueue(ImportJob job) => _channel.Writer.TryWrite(job);

    public IAsyncEnumerable<ImportJob> DequeueAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}

/// <summary>Background worker that runs accepted imports one at a time (rows inside a job run in parallel).</summary>
public sealed class ImportWorker(ImportQueue queue, IServiceScopeFactory scopes) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in queue.DequeueAllAsync(stoppingToken))
        {
            await using var scope = scopes.CreateAsyncScope();
            var runner = scope.ServiceProvider.GetRequiredService<ImportJobRunner>();
            await runner.RunAsync(job, stoppingToken);
        }
    }
}
