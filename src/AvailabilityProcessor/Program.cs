using System.Text.Json.Serialization;
using AvailabilityProcessor.Api;
using AvailabilityProcessor.Configuration;
using AvailabilityProcessor.Dependencies;
using AvailabilityProcessor.Imports;
using AvailabilityProcessor.Messaging;
using AvailabilityProcessor.Persistence;
using AvailabilityProcessor.Processing;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(json =>
{
    json.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    json.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

builder.Services.AddOptions<DependencyOptions>().BindConfiguration(DependencyOptions.Section).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<ResilienceOptions>().BindConfiguration(ResilienceOptions.Section);
builder.Services.AddOptions<BulkImportOptions>().BindConfiguration(BulkImportOptions.Section);
builder.Services.AddOptions<OutboxOptions>().BindConfiguration(OutboxOptions.Section);
builder.Services.AddOptions<StorageOptions>().BindConfiguration(StorageOptions.Section).ValidateDataAnnotations().ValidateOnStart();

builder.Services.AddSingleton(TimeProvider.System);

// Applied versions and the event outbox are kept in SQLite, so they survive a restart.
builder.Services.AddSingleton<SqliteStockStore>();
builder.Services.AddSingleton<IStockStateStore>(services => services.GetRequiredService<SqliteStockStore>());
builder.Services.AddSingleton<IEventOutbox>(services => services.GetRequiredService<SqliteStockStore>());

// Key locks and import jobs are kept in memory.
builder.Services.AddSingleton<StockKeyLock>();
builder.Services.AddSingleton<ImportJobStore>();
builder.Services.AddSingleton<ImportQueue>();

// Processing services are scoped because they depend on typed HttpClients, which should not be captured by singletons.
builder.Services.AddScoped<StockUpdateProcessor>();
builder.Services.AddScoped<SellerInventoryChangedHandler>();
builder.Services.AddScoped<ImportJobRunner>();
builder.Services.AddScoped<OutboxRelay>();

// Product Context API, Availability API Gateway and event sink, each with its own resilience pipeline.
builder.Services.AddDependencyClients();

builder.Services.AddHostedService<ImportWorker>();
builder.Services.AddHostedService<OutboxRelayService>();

var app = builder.Build();

// Open the database now, so that a wrong connection string fails at start-up and not on the first request.
app.Services.GetRequiredService<SqliteStockStore>();

app.UseExceptionHandler();
app.UseStatusCodePages();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapDevEndpoints();
app.MapImportEndpoints();

app.Run();

/// <summary>Exposed for WebApplicationFactory in the tests.</summary>
public partial class Program;
