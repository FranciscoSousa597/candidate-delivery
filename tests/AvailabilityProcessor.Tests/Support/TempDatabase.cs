namespace AvailabilityProcessor.Tests.Support;

/// <summary>
/// A SQLite database file that outlives one <see cref="TestApp"/>, for the tests that restart the service.
/// Dispose it after the applications that use it.
/// </summary>
public sealed class TempDatabase : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"availability-tests-{Guid.NewGuid():N}.db");

    /// <summary>Settings that make a <see cref="TestApp"/> keep its state in this file.</summary>
    public IReadOnlyDictionary<string, string?> Settings =>
        new Dictionary<string, string?> { ["Storage:ConnectionString"] = $"Data Source={_path}" };

    public void Dispose()
    {
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            File.Delete(file);
        }
    }
}
