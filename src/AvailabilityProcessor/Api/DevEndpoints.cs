using System.Text.Json;
using AvailabilityProcessor.Contracts;
using AvailabilityProcessor.Domain;
using AvailabilityProcessor.Messaging;
using AvailabilityProcessor.Processing;

namespace AvailabilityProcessor.Api;

/// <summary>
/// Test-only routes. POST /_dev/messages simulates a broker delivery; in production a consumer adapter calls the same
/// <see cref="SellerInventoryChangedHandler"/> and these routes would be switched off.
/// </summary>
public static class DevEndpoints
{
    public static IEndpointRouteBuilder MapDevEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/_dev/messages", ReceiveMessageAsync);
        app.MapGet("/_dev/stock/{sellerId}/{productId}/{sizeId}", GetStockAsync);
        app.MapGet("/_dev/outbox", async (IEventOutbox outbox, CancellationToken ct) =>
            Results.Ok(new { pending = await outbox.CountPendingAsync(ct), events = await outbox.GetPendingAsync(limit: 100, ct) }));
        return app;
    }

    /// <summary>The HTTP status mirrors what a broker consumer would do with the message.</summary>
    private static async Task<IResult> ReceiveMessageAsync(HttpRequest request, SellerInventoryChangedHandler handler, CancellationToken ct)
    {
        SellerInventoryChanged? message;
        try
        {
            message = await request.ReadFromJsonAsync<SellerInventoryChanged>(ct);
        }
        catch (JsonException ex)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Malformed JSON.", detail: ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(statusCode: StatusCodes.Status415UnsupportedMediaType, title: "Expected application/json.", detail: ex.Message);
        }

        if (message is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Empty message.");
        }

        var result = await handler.HandleAsync(message, ct);
        var statusCode = result switch
        {
            { Code: OutcomeCodes.InvalidInput } => StatusCodes.Status400BadRequest,                          // consumer: dead-letter (poison message)
            { Status: ProcessingStatus.Failed, Retryable: true } => StatusCodes.Status503ServiceUnavailable, // consumer: do not ack, redeliver later
            { Status: ProcessingStatus.Failed } => StatusCodes.Status422UnprocessableEntity,                 // consumer: dead-letter / park
            _ => StatusCodes.Status200OK,                                                                     // applied or skipped: consumer acks
        };

        return Results.Json(result, statusCode: statusCode);
    }

    private static async Task<IResult> GetStockAsync(
        string sellerId,
        string productId,
        string sizeId,
        string countryCode,
        IStockStateStore store,
        CancellationToken ct)
    {
        var key = new StockKey(sellerId, productId, sizeId, countryCode.Trim().ToUpperInvariant());
        return await store.GetAsync(key, ct) is { } stock
            ? Results.Ok(new
            {
                key.SellerId,
                key.ProductId,
                key.SizeId,
                key.CountryCode,
                stock.SourceVersion,
                stock.AvailableQuantity,
                stock.AppliedAt,
            })
            : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "No stock has been applied for this key.");
    }
}
