using System.Net.Http.Json;
using OrderSaga.Contracts;
using Temporalio.Activities;

namespace OrderSaga.Worker.Activities;

public class OrderActivities(HttpClient http, ILogger<OrderActivities> logger)
{
    private static int Attempt => ActivityExecutionContext.Current.Info.Attempt;

    [Activity]
    public async Task CreateOrderAsync(CreateOrderCommand command)
    {
        logger.LogInformation("Creating order {OrderId} (attempt {Attempt})", command.OrderId, Attempt);
        using var request = new HttpRequestMessage(HttpMethod.Put, $"internal/orders/{command.OrderId}")
        {
            Content = JsonContent.Create(new CreateOrderRecordRequest(command.CustomerId, command.Lines, command.TotalAmount, command.Scenario)),
        };
        await ServiceCall.SendAsync(http, request);
    }

    [Activity]
    public async Task ConfirmOrderAsync(ConfirmOrderCommand command)
    {
        logger.LogInformation("Confirming order {OrderId} (attempt {Attempt})", command.OrderId, Attempt);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"internal/orders/{command.OrderId}/confirm");
        await ServiceCall.SendAsync(http, request);
    }

    [Activity]
    public async Task CompleteOrderAsync(CompleteOrderCommand command)
    {
        logger.LogInformation("Completing order {OrderId} (attempt {Attempt})", command.OrderId, Attempt);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"internal/orders/{command.OrderId}/complete");
        await ServiceCall.SendAsync(http, request);
    }

    [Activity]
    public async Task CancelOrderAsync(CancelOrderCommand command)
    {
        logger.LogInformation("Cancelling order {OrderId}: {Reason} (attempt {Attempt})", command.OrderId, command.Reason, Attempt);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"internal/orders/{command.OrderId}/cancel")
        {
            Content = JsonContent.Create(new CancelOrderRequest(command.Reason)),
        };
        await ServiceCall.SendAsync(http, request);
    }
}
