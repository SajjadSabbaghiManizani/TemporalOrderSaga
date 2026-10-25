using Microsoft.Extensions.Options;
using OrderSaga.Contracts;
using Temporalio.Client;
using Temporalio.Exceptions;

namespace OrderSaga.OrderService;

public static class OrderEndpoints
{
    public static void Map(WebApplication app, ITemporalClient client, string taskQueue)
    {
        MapPublicApi(app, client, taskQueue);
        MapInternalApi(app);
    }

    private static void MapPublicApi(WebApplication app, ITemporalClient client, string taskQueue)
    {
        app.MapPost("/orders", async (CreateOrderRequest request, IOptions<OrderApiOptions> options) =>
        {
            var validationError = Validate(request);
            if (validationError is not null)
            {
                return Results.BadRequest(new { error = validationError });
            }

            var orderId = $"ord-{Guid.NewGuid():N}"[..12];
            var total = request.Lines.Sum(line => line.Quantity * line.UnitPrice);
            var input = new OrderWorkflowInput(
                orderId,
                request.CustomerId,
                request.Lines,
                total,
                total >= options.Value.ApprovalThreshold,
                options.Value.ApprovalTimeout,
                request.Scenario);

            var workflowId = WorkflowNames.OrderWorkflowId(orderId);
            await client.StartWorkflowAsync(
                WorkflowNames.Order,
                new object?[] { input },
                new WorkflowOptions(workflowId, taskQueue));

            return Results.Accepted($"/orders/{orderId}", new OrderAccepted(orderId, workflowId, input.RequiresApproval));
        });

        app.MapGet("/orders", (OrderStore store) => Results.Ok(store.List()));

        app.MapGet("/orders/{orderId}", (string orderId, OrderStore store) =>
            store.Find(orderId) is { } order
                ? Results.Ok(order)
                : Results.NotFound(new { error = $"Order {orderId} has not been recorded. The workflow may still be starting, or the id is unknown." }));

        app.MapGet("/orders/{orderId}/workflow", async (string orderId) =>
        {
            var handle = client.GetWorkflowHandle(WorkflowNames.OrderWorkflowId(orderId));
            try
            {
                var description = await handle.DescribeAsync();
                var state = await handle.QueryAsync<OrderWorkflowState>(WorkflowNames.GetStateQuery, Array.Empty<object?>());
                return Results.Ok(new { workflowId = handle.Id, status = description.Status.ToString(), state });
            }
            catch (RpcException)
            {
                return Results.NotFound(new { error = $"No workflow found for order {orderId}" });
            }
        });

        app.MapPost("/orders/{orderId}/approve", async (string orderId, ApprovalRequest? request) =>
        {
            var handle = client.GetWorkflowHandle(WorkflowNames.OrderWorkflowId(orderId));
            try
            {
                await handle.SignalAsync(WorkflowNames.ApproveOrderSignal, new object?[] { request?.ApprovedBy ?? "api" });
                return Results.Accepted(value: new { message = "Signal delivered to the workflow. It does not confirm the approval was applied; query the workflow state." });
            }
            catch (RpcException)
            {
                return Results.NotFound(new { error = $"No running workflow found for order {orderId}" });
            }
        });

        app.MapPost("/orders/{orderId}/reject", async (string orderId, RejectionRequest request) =>
        {
            var handle = client.GetWorkflowHandle(WorkflowNames.OrderWorkflowId(orderId));
            try
            {
                var result = await handle.ExecuteUpdateAsync<RejectionResult>(
                    WorkflowNames.RejectOrderUpdate,
                    new object?[] { new RejectOrderCommand(request.RejectedBy ?? "api", request.Reason) });
                return Results.Ok(result);
            }
            catch (WorkflowUpdateFailedException exception)
            {
                return Results.Conflict(new { error = exception.InnerException?.Message ?? exception.Message });
            }
            catch (RpcException)
            {
                return Results.NotFound(new { error = $"No running workflow found for order {orderId}" });
            }
        });

        app.MapPost("/orders/{orderId}/cancel", async (string orderId) =>
        {
            var handle = client.GetWorkflowHandle(WorkflowNames.OrderWorkflowId(orderId));
            try
            {
                await handle.CancelAsync();
                return Results.Accepted(value: new { message = "Cancellation requested. The workflow compensates before it ends." });
            }
            catch (RpcException)
            {
                return Results.NotFound(new { error = $"No running workflow found for order {orderId}" });
            }
        });

        app.MapPost("/demo/stock-audits", async (int? batches) =>
        {
            var request = new StockAuditRequest(batches ?? 20, TimeSpan.FromSeconds(1));
            var workflowId = $"stock-audit-{Guid.NewGuid():N}"[..20];
            await client.StartWorkflowAsync(
                WorkflowNames.StockAudit,
                new object?[] { request },
                new WorkflowOptions(workflowId, taskQueue));

            return Results.Accepted(value: new { workflowId, request.TotalBatches });
        });
    }

    private static void MapInternalApi(WebApplication app)
    {
        var internalApi = app.MapGroup("/internal/orders");

        internalApi.MapPut("/{orderId}", (string orderId, CreateOrderRecordRequest request, OrderStore store, ILogger<OrderStore> logger) =>
        {
            logger.LogInformation("Order {OrderId}: create record for customer {CustomerId}", orderId, request.CustomerId);
            return ToResult(store.Create(orderId, request));
        });

        internalApi.MapPost("/{orderId}/confirm", (string orderId, OrderStore store, ILogger<OrderStore> logger) =>
        {
            var outcome = store.Confirm(orderId);
            logger.LogInformation("Order {OrderId}: confirm -> {Result}", orderId, outcome.ErrorCode ?? "ok");
            return ToResult(outcome);
        });

        internalApi.MapPost("/{orderId}/complete", (string orderId, OrderStore store, ILogger<OrderStore> logger) =>
        {
            var outcome = store.Complete(orderId);
            logger.LogInformation("Order {OrderId}: complete -> {Result}", orderId, outcome.ErrorCode ?? "ok");
            return ToResult(outcome);
        });

        internalApi.MapPost("/{orderId}/cancel", (string orderId, CancelOrderRequest request, OrderStore store, ILogger<OrderStore> logger) =>
        {
            var outcome = store.Cancel(orderId, request.Reason);
            logger.LogInformation("Order {OrderId}: cancel ({Reason}) -> {Result}", orderId, request.Reason, outcome.ErrorCode ?? "ok");
            return ToResult(outcome);
        });
    }

    private static string? Validate(CreateOrderRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.CustomerId))
        {
            return "customerId is required";
        }

        if (request.Lines is null || request.Lines.Count == 0)
        {
            return "at least one order line is required";
        }

        return request.Lines.Any(line => string.IsNullOrWhiteSpace(line.Sku) || line.Quantity <= 0 || line.UnitPrice <= 0)
            ? "every line needs a sku, a positive quantity and a positive unitPrice"
            : null;
    }

    private static IResult ToResult(ServiceOutcome outcome) =>
        outcome.Succeeded ? Results.NoContent() : Results.UnprocessableEntity(outcome.ToError());
}
