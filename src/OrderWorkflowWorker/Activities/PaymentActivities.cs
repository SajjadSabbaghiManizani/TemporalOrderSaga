using System.Net.Http.Json;
using OrderSaga.Contracts;
using Temporalio.Activities;

namespace OrderSaga.Worker.Activities;

public class PaymentActivities(HttpClient http, ILogger<PaymentActivities> logger)
{
    private static int Attempt => ActivityExecutionContext.Current.Info.Attempt;

    [Activity]
    public async Task<PaymentReceipt> ChargePaymentAsync(ChargePaymentCommand command)
    {
        logger.LogInformation("Charging {Amount} for order {OrderId} (attempt {Attempt})", command.Amount, command.OrderId, Attempt);
        using var request = new HttpRequestMessage(HttpMethod.Put, $"payments/{command.OrderId}")
        {
            Content = JsonContent.Create(new ChargeRequest(command.Amount)),
        };
        return await ServiceCall.SendAsync<PaymentReceipt>(http, request, command.Scenario);
    }

    [Activity]
    public async Task<RefundResult> RefundPaymentAsync(RefundPaymentCommand command)
    {
        logger.LogInformation("Refunding payment for order {OrderId} (attempt {Attempt})", command.OrderId, Attempt);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"payments/{command.OrderId}/refund");
        return await ServiceCall.SendAsync<RefundResult>(http, request);
    }
}
