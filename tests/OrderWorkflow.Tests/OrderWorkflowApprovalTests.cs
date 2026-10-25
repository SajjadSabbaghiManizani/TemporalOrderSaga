using OrderSaga.Contracts;
using Temporalio.Client;

namespace OrderSaga.Tests;

public class OrderWorkflowApprovalTests
{
    [Fact]
    public async Task Order_waits_for_approval_without_touching_inventory_or_payment()
    {
        var activities = new FakeActivities();

        var state = await WorkflowHarness.RunAsync(activities, async (environment, taskQueue) =>
        {
            var handle = await WorkflowHarness.StartOrderAsync(environment, taskQueue, WorkflowHarness.Input(requiresApproval: true));
            return await WorkflowHarness.WaitForPhaseAsync(handle, OrderPhase.AwaitingApproval);
        });

        Assert.Equal(["CreateOrder"], state.CompletedSteps);
        Assert.Equal(["CancelOrder"], state.PendingCompensations);
        Assert.Equal(["CreateOrder"], activities.Calls);
    }

    [Fact]
    public async Task Approval_signal_lets_the_order_continue_to_completion()
    {
        var activities = new FakeActivities();

        var (result, finalState) = await WorkflowHarness.RunAsync(activities, async (environment, taskQueue) =>
        {
            var handle = await WorkflowHarness.StartOrderAsync(environment, taskQueue, WorkflowHarness.Input(requiresApproval: true));
            await WorkflowHarness.WaitForPhaseAsync(handle, OrderPhase.AwaitingApproval);

            await handle.SignalAsync(workflow => workflow.ApproveOrderAsync("alice"));

            var orderResult = await handle.GetResultAsync();
            var state = await handle.QueryAsync(workflow => workflow.GetState());
            return (orderResult, state);
        });

        Assert.Equal(OrderOutcome.Completed, result.Outcome);
        Assert.Equal(OrderPhase.Completed, finalState.Phase);
        Assert.Equal("alice", finalState.Approval?.DecidedBy);
        Assert.Contains("Approval", finalState.CompletedSteps);
    }

    [Fact]
    public async Task Rejection_update_cancels_the_order_before_any_resource_is_reserved()
    {
        var activities = new FakeActivities();

        var (rejection, result) = await WorkflowHarness.RunAsync(activities, async (environment, taskQueue) =>
        {
            var handle = await WorkflowHarness.StartOrderAsync(environment, taskQueue, WorkflowHarness.Input(requiresApproval: true));
            await WorkflowHarness.WaitForPhaseAsync(handle, OrderPhase.AwaitingApproval);

            var updateResult = await handle.ExecuteUpdateAsync(
                workflow => workflow.RejectOrderAsync(new RejectOrderCommand("bob", "suspected fraud")));

            return (updateResult, await handle.GetResultAsync());
        });

        Assert.Equal("ord-test", rejection.OrderId);
        Assert.Equal(OrderOutcome.Cancelled, result.Outcome);
        Assert.Contains("suspected fraud", result.Reason);
        Assert.Equal(["CreateOrder", "CancelOrder"], activities.Calls);
    }

    [Fact]
    public async Task Missing_approval_times_out_and_cancels_the_order()
    {
        var activities = new FakeActivities();
        var input = WorkflowHarness.Input(requiresApproval: true, approvalTimeout: TimeSpan.FromMinutes(30));

        var result = await WorkflowHarness.ExecuteOrderAsync(activities, input);

        Assert.Equal(OrderOutcome.Cancelled, result.Outcome);
        Assert.Contains("No approval decision", result.Reason);
        Assert.Equal(["CreateOrder", "CancelOrder"], activities.Calls);
    }

    [Fact]
    public async Task Cancelling_a_waiting_order_compensates_and_ends_the_workflow_as_cancelled()
    {
        var activities = new FakeActivities();

        await WorkflowHarness.RunAsync(activities, async (environment, taskQueue) =>
        {
            var handle = await WorkflowHarness.StartOrderAsync(environment, taskQueue, WorkflowHarness.Input(requiresApproval: true));
            await WorkflowHarness.WaitForPhaseAsync(handle, OrderPhase.AwaitingApproval);

            await handle.CancelAsync();

            await Assert.ThrowsAsync<WorkflowFailedException>(() => handle.GetResultAsync());
            return 0;
        });

        Assert.Equal(["CreateOrder", "CancelOrder"], activities.Calls);
    }
}
