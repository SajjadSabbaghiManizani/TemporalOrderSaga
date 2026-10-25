using OrderSaga.Contracts;
using Temporalio.Client;
using Temporalio.Exceptions;

namespace OrderSaga.Tests;

public class OrderWorkflowSagaTests
{
    [Fact]
    public async Task Successful_order_runs_every_step_in_order_and_completes()
    {
        var activities = new FakeActivities();

        var result = await WorkflowHarness.ExecuteOrderAsync(activities, WorkflowHarness.Input());

        Assert.Equal(OrderOutcome.Completed, result.Outcome);
        Assert.Equal(
            ["CreateOrder", "ReserveInventory", "ChargePayment", "ConfirmOrder", "FinalizeInventory", "CompleteOrder"],
            activities.Calls);
    }

    [Fact]
    public async Task Declined_payment_releases_inventory_and_cancels_without_refunding()
    {
        var activities = new FakeActivities
        {
            ChargeFailure = _ => FakeActivities.Rejected(BusinessErrorCodes.PaymentDeclined),
        };

        var result = await WorkflowHarness.ExecuteOrderAsync(activities, WorkflowHarness.Input());

        Assert.Equal(OrderOutcome.Cancelled, result.Outcome);
        Assert.Contains(BusinessErrorCodes.PaymentDeclined, result.Reason);
        Assert.Equal(
            ["CreateOrder", "ReserveInventory", "ChargePayment", "ReleaseInventory", "CancelOrder"],
            activities.Calls);
    }

    [Fact]
    public async Task Confirmation_failure_after_payment_refunds_then_releases_then_cancels()
    {
        var activities = new FakeActivities
        {
            ConfirmFailure = _ => FakeActivities.Rejected(BusinessErrorCodes.OrderCannotBeConfirmed),
        };

        var result = await WorkflowHarness.ExecuteOrderAsync(activities, WorkflowHarness.Input());

        Assert.Equal(OrderOutcome.Cancelled, result.Outcome);
        Assert.Equal(
            ["CreateOrder", "ReserveInventory", "ChargePayment", "ConfirmOrder", "RefundPayment", "ReleaseInventory", "CancelOrder"],
            activities.Calls);
    }

    [Fact]
    public async Task Out_of_stock_cancels_without_releasing_inventory_that_was_never_reserved()
    {
        var activities = new FakeActivities
        {
            ReserveFailure = _ => FakeActivities.Rejected(BusinessErrorCodes.OutOfStock),
        };

        var result = await WorkflowHarness.ExecuteOrderAsync(activities, WorkflowHarness.Input());

        Assert.Equal(OrderOutcome.Cancelled, result.Outcome);
        Assert.Equal(["CreateOrder", "ReserveInventory", "CancelOrder"], activities.Calls);
    }

    [Fact]
    public async Task Transient_failures_are_retried_until_the_activity_succeeds()
    {
        var activities = new FakeActivities
        {
            ReserveFailure = attempt => attempt <= 2 ? FakeActivities.Outage() : null,
        };

        var result = await WorkflowHarness.ExecuteOrderAsync(activities, WorkflowHarness.Input());

        Assert.Equal(OrderOutcome.Completed, result.Outcome);
        Assert.Equal(3, activities.Calls.Count(call => call == "ReserveInventory"));
    }

    [Fact]
    public async Task Charge_with_unknown_outcome_is_reconciled_by_the_idempotent_refund()
    {
        var activities = new FakeActivities
        {
            ChargeFailure = _ => FakeActivities.Outage(),
        };

        var result = await WorkflowHarness.ExecuteOrderAsync(activities, WorkflowHarness.Input());

        Assert.Equal(OrderOutcome.Cancelled, result.Outcome);
        Assert.Equal(5, activities.Calls.Count(call => call == "ChargePayment"));
        Assert.Equal(
            ["RefundPayment", "ReleaseInventory", "CancelOrder"],
            activities.Calls.Skip(activities.Calls.Length - 3));
    }

    [Fact]
    public async Task Compensation_is_retried_until_it_succeeds()
    {
        var activities = new FakeActivities
        {
            ConfirmFailure = _ => FakeActivities.Rejected(BusinessErrorCodes.OrderCannotBeConfirmed),
            RefundFailure = attempt => attempt <= 2 ? FakeActivities.Outage() : null,
        };

        var result = await WorkflowHarness.ExecuteOrderAsync(activities, WorkflowHarness.Input());

        Assert.Equal(OrderOutcome.Cancelled, result.Outcome);
        Assert.Equal(3, activities.Calls.Count(call => call == "RefundPayment"));
        Assert.Equal("CancelOrder", activities.Calls[^1]);
    }

    [Fact]
    public async Task Compensation_that_cannot_succeed_still_runs_independent_compensations_then_fails_the_workflow()
    {
        var activities = new FakeActivities
        {
            ConfirmFailure = _ => FakeActivities.Rejected(BusinessErrorCodes.OrderCannotBeConfirmed),
            RefundFailure = _ => FakeActivities.Rejected(BusinessErrorCodes.OrderAlreadyCompleted),
        };

        var exception = await Assert.ThrowsAsync<WorkflowFailedException>(() =>
            WorkflowHarness.ExecuteOrderAsync(activities, WorkflowHarness.Input()));

        var failure = Assert.IsType<ApplicationFailureException>(exception.InnerException);
        Assert.Equal("CompensationIncomplete", failure.ErrorType);
        Assert.Equal(1, activities.Calls.Count(call => call == "RefundPayment"));
        Assert.Contains("ReleaseInventory", activities.Calls);
        Assert.Contains("CancelOrder", activities.Calls);
    }
}
