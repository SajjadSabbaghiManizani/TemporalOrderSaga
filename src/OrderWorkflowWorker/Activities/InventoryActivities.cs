using System.Net.Http.Json;
using OrderSaga.Contracts;
using Temporalio.Activities;

namespace OrderSaga.Worker.Activities;

public class InventoryActivities(HttpClient http, ILogger<InventoryActivities> logger)
{
    private static int Attempt => ActivityExecutionContext.Current.Info.Attempt;

    [Activity]
    public async Task ReserveInventoryAsync(ReserveInventoryCommand command)
    {
        logger.LogInformation("Reserving inventory for order {OrderId} (attempt {Attempt})", command.OrderId, Attempt);
        using var request = new HttpRequestMessage(HttpMethod.Put, $"reservations/{command.OrderId}")
        {
            Content = JsonContent.Create(new ReserveInventoryRequest(command.Lines)),
        };
        await ServiceCall.SendAsync(http, request, command.Scenario);
    }

    [Activity]
    public async Task ReleaseInventoryAsync(ReleaseInventoryCommand command)
    {
        logger.LogInformation("Releasing inventory for order {OrderId} (attempt {Attempt})", command.OrderId, Attempt);
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"reservations/{command.OrderId}");
        await ServiceCall.SendAsync(http, request);
    }

    [Activity]
    public async Task FinalizeInventoryAsync(FinalizeInventoryCommand command)
    {
        logger.LogInformation("Finalizing inventory reservation for order {OrderId} (attempt {Attempt})", command.OrderId, Attempt);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"reservations/{command.OrderId}/finalize");
        await ServiceCall.SendAsync(http, request);
    }
}
