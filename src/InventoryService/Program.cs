using OrderSaga.Contracts;
using OrderSaga.InventoryService;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<InventoryStore>();

var app = builder.Build();

app.MapPut("/reservations/{orderId}", (string orderId, ReserveInventoryRequest request, HttpRequest http, InventoryStore store, ILogger<Program> logger) =>
{
    var scenario = http.Headers[DemoScenarios.HeaderName].ToString();
    if (scenario == DemoScenarios.TransientInventory && store.CountAttempt(orderId) <= 2)
    {
        logger.LogWarning("Order {OrderId}: simulating a transient inventory outage", orderId);
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    var outcome = store.Reserve(orderId, request.Lines);
    logger.LogInformation("Order {OrderId}: reserve inventory -> {Result}", orderId, outcome.ErrorCode ?? "ok");
    return ToResult(outcome);
});

app.MapDelete("/reservations/{orderId}", (string orderId, InventoryStore store, ILogger<Program> logger) =>
{
    var outcome = store.Release(orderId);
    logger.LogInformation("Order {OrderId}: release inventory -> {Result}", orderId, outcome.ErrorCode ?? "ok");
    return ToResult(outcome);
});

app.MapPost("/reservations/{orderId}/finalize", (string orderId, InventoryStore store, ILogger<Program> logger) =>
{
    var outcome = store.FinalizeReservation(orderId);
    logger.LogInformation("Order {OrderId}: finalize reservation -> {Result}", orderId, outcome.ErrorCode ?? "ok");
    return ToResult(outcome);
});

app.MapGet("/reservations/{orderId}", (string orderId, InventoryStore store) =>
    store.FindReservation(orderId) is { } reservation
        ? Results.Ok(reservation)
        : Results.NotFound(new BusinessError(BusinessErrorCodes.ReservationNotFound, $"No reservation for {orderId}")));

app.MapGet("/stock", (InventoryStore store) => Results.Ok(store.Snapshot()));

app.Run();

static IResult ToResult(ServiceOutcome outcome) =>
    outcome.Succeeded ? Results.NoContent() : Results.UnprocessableEntity(outcome.ToError());
