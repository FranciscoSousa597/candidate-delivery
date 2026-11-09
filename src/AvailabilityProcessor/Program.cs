using System.Text.Json.Serialization;
using AvailabilityProcessor.Api;
using AvailabilityProcessor.Configuration;
using AvailabilityProcessor.Dependencies;
using AvailabilityProcessor.Imports;
using AvailabilityProcessor.Messaging;
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

builder.Services.AddSingleton(TimeProvider.System);

// Shared state: applied versions, per-key locks, outbox and import jobs (in memory for the exercise).
builder.Services.AddSingleton<IStockStateStore, InMemoryStockStateStore>();
builder.Services.AddSingleton<StockKeyLock>();
builder.Services.AddSingleton<PendingEventOutbox>();
builder.Services.AddSingleton<ImportJobStore>();
builder.Services.AddSingleton<ImportQueue>();

// Processing services are scoped because they depend on typed HttpClients, which should not be captured by singletons.
builder.Services.AddScoped<StockUpdateProcessor>();
builder.Services.AddScoped<SellerInventoryChangedHandler>();
builder.Services.AddScoped<ImportJobRunner>();

// Product Context API, Availability API Gateway and event sink, each with its own resilience pipeline.
builder.Services.AddDependencyClients();

builder.Services.AddHostedService<ImportWorker>();
builder.Services.AddHostedService<OutboxRelayService>();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapDevEndpoints();
app.MapImportEndpoints();

app.Run();

/// <summary>Exposed for WebApplicationFactory in the tests.</summary>
public partial class Program;
