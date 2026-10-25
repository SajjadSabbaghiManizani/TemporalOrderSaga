using Microsoft.Extensions.Options;
using OrderSaga.Contracts;
using OrderSaga.Worker.Activities;
using OrderSaga.Worker.Workflows;
using Temporalio.Worker;

namespace OrderSaga.Worker;

public sealed class OrderWorker(
    IServiceProvider services,
    IOptions<TemporalSettings> settings,
    ILoggerFactory loggerFactory,
    ILogger<OrderWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var temporal = settings.Value;
        var client = await TemporalConnector.ConnectAsync(temporal, loggerFactory, stoppingToken);

        var options = new TemporalWorkerOptions(temporal.TaskQueue)
            .AddAllActivities(services.GetRequiredService<OrderActivities>())
            .AddAllActivities(services.GetRequiredService<InventoryActivities>())
            .AddAllActivities(services.GetRequiredService<PaymentActivities>())
            .AddAllActivities(services.GetRequiredService<StockAuditActivities>())
            .AddWorkflow<OrderWorkflow>()
            .AddWorkflow<StockAuditWorkflow>();

        using var worker = new TemporalWorker(client, options);
        logger.LogInformation("Worker polling task queue {TaskQueue} on {Address}", temporal.TaskQueue, temporal.Address);

        try
        {
            await worker.ExecuteAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("Worker stopped");
        }
    }
}
