using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AvailabilityProcessor.Tests.Support;

/// <summary>The real application, with its HTTP dependencies replaced by <see cref="FakeDependencies"/>.</summary>
public sealed class TestApp : WebApplicationFactory<Program>
{
    private readonly Dictionary<string, string?> _settings = new()
    {
        // Same resilience pipeline as production, with short delays.
        ["Resilience:MaxRetryAttempts"] = "2",
        ["Resilience:BaseRetryDelay"] = "00:00:00.001",
        ["Resilience:MaxRetryDelay"] = "00:00:00.010",

        // The outbox relay is driven explicitly by the tests that need it.
        ["Outbox:RelayInterval"] = "01:00:00",

        // Each application gets its own database, in memory, so tests do not share state or leave files behind.
        ["Storage:ConnectionString"] = "Data Source=:memory:",
    };

    public TestApp()
    {
    }

    /// <summary>Starts the application with extra or different settings, for the tests that need them.</summary>
    public TestApp(IReadOnlyDictionary<string, string?> settings)
    {
        foreach (var (key, value) in settings)
        {
            _settings[key] = value;
        }
    }

    public FakeDependencies Dependencies { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(_settings));
        builder.ConfigureTestServices(services =>
            services.ConfigureHttpClientDefaults(http => http.ConfigurePrimaryHttpMessageHandler(() => Dependencies)));
    }
}
