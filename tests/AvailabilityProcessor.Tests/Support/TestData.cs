namespace AvailabilityProcessor.Tests.Support;

/// <summary>Paths to the fixtures shipped with the exercise (wiremock/__files).</summary>
public static class TestData
{
    public static string RepoRoot { get; } = FindRepoRoot();

    public static string FixturesDirectory => Path.Combine(RepoRoot, "wiremock", "__files");

    public static string SampleEvent => Path.Combine(FixturesDirectory, "sample-event.json");

    public static string BulkCsv => Path.Combine(FixturesDirectory, "bulk-stock.csv");

    public static string SnapshotPt => Path.Combine(FixturesDirectory, "product-contexts-PT.json");

    private static string FindRepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "compose.yaml")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Could not find the repository root (the folder that contains compose.yaml).");
    }
}
