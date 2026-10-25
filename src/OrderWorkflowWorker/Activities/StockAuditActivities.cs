using OrderSaga.Contracts;
using Temporalio.Activities;

namespace OrderSaga.Worker.Activities;

public class StockAuditActivities(ILogger<StockAuditActivities> logger)
{
    [Activity]
    public async Task<StockAuditResult> AuditStockAsync(StockAuditRequest request)
    {
        var context = ActivityExecutionContext.Current;
        var lastRecordedBatch = context.Info.HeartbeatDetails.Count > 0
            ? await context.Info.HeartbeatDetailAtAsync<int>(0)
            : 0;
        var resumeFromBatch = Math.Clamp(lastRecordedBatch, 0, request.TotalBatches);

        logger.LogInformation(
            "Stock audit attempt {Attempt}: resuming from batch {ResumeFromBatch} of {TotalBatches}",
            context.Info.Attempt,
            resumeFromBatch,
            request.TotalBatches);

        for (var batch = resumeFromBatch; batch < request.TotalBatches; batch++)
        {
            await Task.Delay(request.BatchDuration, context.CancellationToken);
            logger.LogInformation("Audited batch {Batch} of {TotalBatches}", batch + 1, request.TotalBatches);

            // The SDK throttles heartbeats and sends them in the background, so the progress the server holds can
            // trail what was actually done. A retry may therefore repeat a few batches: each batch must be idempotent.
            context.Heartbeat(batch + 1);
        }

        return new StockAuditResult(request.TotalBatches, resumeFromBatch, context.Info.Attempt);
    }
}
