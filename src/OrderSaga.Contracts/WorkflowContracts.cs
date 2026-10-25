using System.Text.Json.Serialization;

namespace OrderSaga.Contracts;

public static class TaskQueues
{
    public const string OrderSaga = "order-saga";
}

public static class WorkflowNames
{
    public const string Order = "OrderWorkflow";
    public const string StockAudit = "StockAuditWorkflow";

    public const string ApproveOrderSignal = "ApproveOrder";
    public const string RejectOrderUpdate = "RejectOrder";
    public const string GetStateQuery = "GetState";

    public static string OrderWorkflowId(string orderId) => $"order-{orderId}";
}

public class TemporalSettings
{
    public string Address { get; set; } = "localhost:7233";

    public string Namespace { get; set; } = "default";

    public string TaskQueue { get; set; } = TaskQueues.OrderSaga;
}

public record OrderWorkflowInput(
    string OrderId,
    string CustomerId,
    IReadOnlyList<OrderLine> Lines,
    decimal TotalAmount,
    bool RequiresApproval,
    TimeSpan ApprovalTimeout,
    string? Scenario);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OrderOutcome
{
    Completed,
    Cancelled,
}

public record OrderResult(string OrderId, OrderOutcome Outcome, string? Reason);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OrderPhase
{
    Started,
    CreatingOrder,
    AwaitingApproval,
    ReservingInventory,
    ChargingPayment,
    ConfirmingOrder,
    FinalizingInventory,
    CompletingOrder,
    Completed,
    Compensating,
    Cancelled,
    ManualInterventionRequired,
}

public record ApprovalDecision(bool Approved, string DecidedBy, string? Reason);

public record RejectOrderCommand(string RejectedBy, string Reason);

public record RejectionResult(string OrderId, string Message);

public record OrderWorkflowState(
    OrderPhase Phase,
    IReadOnlyList<string> CompletedSteps,
    IReadOnlyList<string> PendingCompensations,
    IReadOnlyList<string> CompletedCompensations,
    ApprovalDecision? Approval,
    string? FailureReason);

public record StockAuditRequest(int TotalBatches, TimeSpan BatchDuration);

public record StockAuditResult(int TotalBatches, int ResumedFromBatch, int Attempt);
