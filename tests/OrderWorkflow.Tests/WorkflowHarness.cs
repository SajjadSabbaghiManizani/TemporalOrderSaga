using OrderSaga.Contracts;
using OrderSaga.Worker.Workflows;
using Temporalio.Client;
using Temporalio.Testing;
using Temporalio.Worker;

namespace OrderSaga.Tests;

internal static class WorkflowHarness
{
    public static OrderWorkflowInput Input(bool requiresApproval = false, TimeSpan? approvalTimeout = null) => new(
        OrderId: "ord-test",
        CustomerId: "cust-1",
        Lines: [new OrderLine("WIDGET", 2, 10m)],
        TotalAmount: 20m,
        RequiresApproval: requiresApproval,
        ApprovalTimeout: approvalTimeout ?? TimeSpan.FromHours(1),
        Scenario: null);

    public static Task<OrderResult> ExecuteOrderAsync(FakeActivities activities, OrderWorkflowInput input) =>
        RunAsync(activities, (environment, taskQueue) =>
            environment.Client.ExecuteWorkflowAsync(
                (OrderWorkflow workflow) => workflow.RunAsync(input),
                new WorkflowOptions($"order-{Guid.NewGuid():N}", taskQueue)));

    public static async Task<T> RunAsync<T>(FakeActivities activities, Func<WorkflowEnvironment, string, Task<T>> scenario)
    {
        await using var environment = await WorkflowEnvironment.StartTimeSkippingAsync();
        var taskQueue = $"test-{Guid.NewGuid():N}";

        using var worker = new TemporalWorker(
            environment.Client,
            new TemporalWorkerOptions(taskQueue)
                .AddAllActivities(activities)
                .AddWorkflow<OrderWorkflow>());

        return await worker.ExecuteAsync(() => scenario(environment, taskQueue));
    }

    public static async Task<WorkflowHandle<OrderWorkflow, OrderResult>> StartOrderAsync(
        WorkflowEnvironment environment, string taskQueue, OrderWorkflowInput input) =>
        await environment.Client.StartWorkflowAsync(
            (OrderWorkflow workflow) => workflow.RunAsync(input),
            new WorkflowOptions($"order-{Guid.NewGuid():N}", taskQueue));

    public static async Task<OrderWorkflowState> WaitForPhaseAsync(WorkflowHandle<OrderWorkflow, OrderResult> handle, OrderPhase phase)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var state = await handle.QueryAsync(workflow => workflow.GetState());
                if (state.Phase == phase)
                {
                    return state;
                }
            }
            catch (Exception)
            {
                // The first workflow task may not have completed yet; poll again.
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"The workflow never reached phase {phase}");
    }
}
