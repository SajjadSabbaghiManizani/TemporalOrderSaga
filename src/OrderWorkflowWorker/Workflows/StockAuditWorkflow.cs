using OrderSaga.Contracts;
using OrderSaga.Worker.Activities;
using Temporalio.Workflows;

namespace OrderSaga.Worker.Workflows;

[Workflow(WorkflowNames.StockAudit)]
public class StockAuditWorkflow
{
    [WorkflowRun]
    public Task<StockAuditResult> RunAsync(StockAuditRequest request) =>
        Workflow.ExecuteActivityAsync(
            (StockAuditActivities activities) => activities.AuditStockAsync(request),
            ActivityProfiles.HeartbeatingJob());
}
