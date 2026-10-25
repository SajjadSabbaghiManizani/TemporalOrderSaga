using OrderSaga.Contracts;
using OrderSaga.PaymentService;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<PaymentStore>();

var app = builder.Build();

// Longer than the worker's 5 second HTTP timeout, so the caller gives up while the charge is already recorded.
var timeoutAfterChargeDelay = TimeSpan.FromSeconds(8);

app.MapPut("/payments/{orderId}", async (string orderId, ChargeRequest request, HttpRequest http, PaymentStore store, ILogger<Program> logger) =>
{
    if (request.Amount <= 0)
    {
        return Results.BadRequest(new { error = "Amount must be positive" });
    }

    var scenario = http.Headers[DemoScenarios.HeaderName].ToString();
    var (outcome, payment, created) = store.Charge(orderId, request.Amount, scenario == DemoScenarios.PaymentDeclined);
    if (!outcome.Succeeded)
    {
        logger.LogInformation("Order {OrderId}: charge rejected -> {Result}", orderId, outcome.ErrorCode);
        return Results.UnprocessableEntity(outcome.ToError());
    }

    logger.LogInformation("Order {OrderId}: charge {Amount} -> {Result}", orderId, request.Amount, created ? "created" : "replayed existing charge");

    if (created && scenario == DemoScenarios.PaymentTimeoutAfterCharge)
    {
        logger.LogWarning("Order {OrderId}: charge recorded, delaying the response to simulate a lost reply", orderId);
        await Task.Delay(timeoutAfterChargeDelay);
    }

    return Results.Ok(payment!.ToReceipt());
});

app.MapPost("/payments/{orderId}/refund", (string orderId, PaymentStore store, ILogger<Program> logger) =>
{
    if (store.RefundOutage)
    {
        logger.LogWarning("Order {OrderId}: refund rejected, refund outage is enabled", orderId);
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    var outcome = store.Refund(orderId);
    logger.LogInformation("Order {OrderId}: refund -> {Outcome}", orderId, outcome);
    return Results.Ok(new RefundResult(orderId, outcome));
});

app.MapGet("/payments/{orderId}", (string orderId, PaymentStore store) =>
    store.Find(orderId) is { } payment
        ? Results.Ok(payment.ToReceipt())
        : Results.NotFound(new { error = $"No payment for {orderId}" }));

app.MapPut("/admin/refund-outage", (RefundOutageRequest request, PaymentStore store, ILogger<Program> logger) =>
{
    store.RefundOutage = request.Enabled;
    logger.LogWarning("Refund outage set to {Enabled}", request.Enabled);
    return Results.Ok(new { refundOutage = store.RefundOutage });
});

app.Run();
