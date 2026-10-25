using OrderSaga.Contracts;
using OrderSaga.Worker.Activities;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace OrderSaga.Worker.Workflows;

[Workflow(WorkflowNames.Order)]
public class OrderWorkflow
{
    private readonly Stack<Compensation> pendingCompensations = new();
    private readonly List<string> completedSteps = new();
    private readonly List<string> completedCompensations = new();

    private string orderId = string.Empty;
    private OrderPhase phase = OrderPhase.Started;
    private ApprovalDecision? approval;
    private string? failureReason;

    [WorkflowRun]
    public async Task<OrderResult> RunAsync(OrderWorkflowInput input)
    {
        orderId = input.OrderId;

        try
        {
            await ProcessOrderAsync(input);
            phase = OrderPhase.Completed;
            Workflow.Logger.LogInformation("Order {OrderId} completed", orderId);
            return new OrderResult(orderId, OrderOutcome.Completed, null);
        }
        catch (Exception exception) when (TemporalException.IsCanceledException(exception))
        {
            // A caller cancelling the workflow is not a business failure: compensate, then end as Canceled.
            failureReason = "Cancelled by caller";
            await CompensateAsync();
            phase = OrderPhase.Cancelled;
            throw;
        }
        catch (Exception exception) when (exception is ActivityFailureException or OrderNotApprovedException)
        {
            // A business or infrastructure failure: compensate, then report a normal "Cancelled" outcome.
            failureReason = Describe(exception);
            Workflow.Logger.LogWarning("Order {OrderId} failed: {Reason}", orderId, failureReason);
            await CompensateAsync();
            phase = OrderPhase.Cancelled;
            return new OrderResult(orderId, OrderOutcome.Cancelled, failureReason);
        }
    }

    [WorkflowSignal(WorkflowNames.ApproveOrderSignal)]
    public Task ApproveOrderAsync(string approvedBy)
    {
        if (approval is null)
        {
            approval = new ApprovalDecision(true, approvedBy, null);
        }
        else
        {
            Workflow.Logger.LogInformation("Ignoring approval from {ApprovedBy}: a decision was already made", approvedBy);
        }

        return Task.CompletedTask;
    }

    [WorkflowUpdate(WorkflowNames.RejectOrderUpdate)]
    public Task<RejectionResult> RejectOrderAsync(RejectOrderCommand command)
    {
        approval = new ApprovalDecision(false, command.RejectedBy, command.Reason);
        return Task.FromResult(new RejectionResult(orderId, "Rejection accepted; the order will be cancelled"));
    }

    [WorkflowUpdateValidator(nameof(RejectOrderAsync))]
    public void ValidateRejectOrder(RejectOrderCommand command)
    {
        if (phase != OrderPhase.AwaitingApproval || approval is not null)
        {
            throw new ApplicationFailureException($"The order cannot be rejected now (phase: {phase})", errorType: "RejectionNotAllowed");
        }

        if (string.IsNullOrWhiteSpace(command.Reason))
        {
            throw new ApplicationFailureException("A rejection reason is required", errorType: "InvalidRejection");
        }
    }

    [WorkflowQuery(WorkflowNames.GetStateQuery)]
    public OrderWorkflowState GetState() => new(
        phase,
        completedSteps.ToList(),
        pendingCompensations.Select(compensation => compensation.Name).ToList(),
        completedCompensations.ToList(),
        approval,
        failureReason);

    private async Task ProcessOrderAsync(OrderWorkflowInput input)
    {
        // Registered before CreateOrder runs: cancelling an order that was never recorded is a harmless no-op,
        // so this stays correct even if CreateOrder fails with an unknown outcome.
        pendingCompensations.Push(new Compensation(OrderSteps.CancelOrder, CancelOrderAsync));

        phase = OrderPhase.CreatingOrder;
        await Workflow.ExecuteActivityAsync(
            (OrderActivities activities) => activities.CreateOrderAsync(
                new CreateOrderCommand(input.OrderId, input.CustomerId, input.Lines, input.TotalAmount, input.Scenario)),
            ActivityProfiles.Forward());
        completedSteps.Add(OrderSteps.CreateOrder);

        if (input.RequiresApproval)
        {
            await WaitForApprovalAsync(input.ApprovalTimeout);
        }

        await ReserveInventoryAsync(input);
        await ChargePaymentAsync(input);

        phase = OrderPhase.ConfirmingOrder;
        await Workflow.ExecuteActivityAsync(
            (OrderActivities activities) => activities.ConfirmOrderAsync(new ConfirmOrderCommand(input.OrderId)),
            ActivityProfiles.Forward());
        completedSteps.Add(OrderSteps.ConfirmOrder);

        phase = OrderPhase.FinalizingInventory;
        await Workflow.ExecuteActivityAsync(
            (InventoryActivities activities) => activities.FinalizeInventoryAsync(new FinalizeInventoryCommand(input.OrderId)),
            ActivityProfiles.PointOfNoReturn());
        completedSteps.Add(OrderSteps.FinalizeInventory);

        phase = OrderPhase.CompletingOrder;
        await Workflow.ExecuteActivityAsync(
            (OrderActivities activities) => activities.CompleteOrderAsync(new CompleteOrderCommand(input.OrderId)),
            ActivityProfiles.PointOfNoReturn());
        completedSteps.Add(OrderSteps.CompleteOrder);
    }

    private async Task WaitForApprovalAsync(TimeSpan timeout)
    {
        phase = OrderPhase.AwaitingApproval;

        var decided = await Workflow.WaitConditionAsync(() => approval is not null, timeout);
        if (!decided)
        {
            throw new OrderNotApprovedException($"No approval decision within {timeout}");
        }

        if (!approval!.Approved)
        {
            throw new OrderNotApprovedException($"Rejected by {approval.DecidedBy}: {approval.Reason}");
        }

        completedSteps.Add(OrderSteps.Approval);
    }

    private async Task ReserveInventoryAsync(OrderWorkflowInput input)
    {
        phase = OrderPhase.ReservingInventory;
        try
        {
            await Workflow.ExecuteActivityAsync(
                (InventoryActivities activities) => activities.ReserveInventoryAsync(
                    new ReserveInventoryCommand(input.OrderId, input.Lines, input.Scenario)),
                ActivityProfiles.Forward());
        }
        catch (Exception exception) when (!WasRejectedWith(exception, BusinessErrorCodes.OutOfStock, BusinessErrorCodes.UnknownSku))
        {
            // The outcome is unknown (timeouts, exhausted retries, cancellation): stock may be held, so release it.
            pendingCompensations.Push(new Compensation(OrderSteps.ReleaseInventory, ReleaseInventoryAsync));
            throw;
        }

        pendingCompensations.Push(new Compensation(OrderSteps.ReleaseInventory, ReleaseInventoryAsync));
        completedSteps.Add(OrderSteps.ReserveInventory);

        Task ReleaseInventoryAsync() => Workflow.ExecuteActivityAsync(
            (InventoryActivities activities) => activities.ReleaseInventoryAsync(new ReleaseInventoryCommand(input.OrderId)),
            ActivityProfiles.Compensation());
    }

    private async Task ChargePaymentAsync(OrderWorkflowInput input)
    {
        phase = OrderPhase.ChargingPayment;
        try
        {
            await Workflow.ExecuteActivityAsync(
                (PaymentActivities activities) => activities.ChargePaymentAsync(
                    new ChargePaymentCommand(input.OrderId, input.TotalAmount, input.Scenario)),
                ActivityProfiles.Forward());
        }
        catch (Exception exception) when (!WasRejectedWith(exception, BusinessErrorCodes.PaymentDeclined, BusinessErrorCodes.PaymentVoided))
        {
            // A timed out charge may have been captured. The refund is idempotent: it refunds a captured
            // charge, and if none exists it voids the order's payment so a late charge cannot land.
            pendingCompensations.Push(new Compensation(OrderSteps.RefundPayment, RefundPaymentAsync));
            throw;
        }

        pendingCompensations.Push(new Compensation(OrderSteps.RefundPayment, RefundPaymentAsync));
        completedSteps.Add(OrderSteps.ChargePayment);

        Task RefundPaymentAsync() => Workflow.ExecuteActivityAsync(
            (PaymentActivities activities) => activities.RefundPaymentAsync(new RefundPaymentCommand(input.OrderId)),
            ActivityProfiles.Compensation());
    }

    private Task CancelOrderAsync() => Workflow.ExecuteActivityAsync(
        (OrderActivities activities) => activities.CancelOrderAsync(new CancelOrderCommand(orderId, failureReason ?? "Unknown")),
        ActivityProfiles.Compensation());

    private async Task CompensateAsync()
    {
        phase = OrderPhase.Compensating;
        var failures = new List<string>();

        while (pendingCompensations.TryPop(out var compensation))
        {
            try
            {
                await compensation.RunAsync();
                completedCompensations.Add(compensation.Name);
            }
            catch (ActivityFailureException exception)
            {
                failures.Add($"{compensation.Name}: {Describe(exception)}");
            }
        }

        if (failures.Count > 0)
        {
            phase = OrderPhase.ManualInterventionRequired;
            throw new ApplicationFailureException(
                $"Compensation incomplete, manual intervention required: {string.Join("; ", failures)}",
                errorType: "CompensationIncomplete",
                nonRetryable: true);
        }
    }

    private static bool WasRejectedWith(Exception exception, params string[] errorTypes) =>
        exception is ActivityFailureException { InnerException: ApplicationFailureException { ErrorType: { } errorType } }
        && errorTypes.Contains(errorType);

    private static string Describe(Exception exception) => exception switch
    {
        ActivityFailureException { InnerException: ApplicationFailureException application } =>
            $"{application.ErrorType ?? "ActivityFailure"}: {application.Message}",
        ActivityFailureException { InnerException: { } inner } => $"{inner.GetType().Name}: {inner.Message}",
        _ => exception.Message,
    };

    private sealed record Compensation(string Name, Func<Task> RunAsync);

    private sealed class OrderNotApprovedException(string message) : Exception(message);

    private static class OrderSteps
    {
        public const string CreateOrder = "CreateOrder";
        public const string Approval = "Approval";
        public const string ReserveInventory = "ReserveInventory";
        public const string ChargePayment = "ChargePayment";
        public const string ConfirmOrder = "ConfirmOrder";
        public const string FinalizeInventory = "FinalizeInventory";
        public const string CompleteOrder = "CompleteOrder";
        public const string ReleaseInventory = "ReleaseInventory";
        public const string RefundPayment = "RefundPayment";
        public const string CancelOrder = "CancelOrder";
    }
}
