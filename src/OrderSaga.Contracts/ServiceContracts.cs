using System.Text.Json.Serialization;

namespace OrderSaga.Contracts;

public record OrderLine(string Sku, int Quantity, decimal UnitPrice);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OrderStatus
{
    Created,
    Confirmed,
    Completed,
    Cancelled,
}

public record CreateOrderRequest(string CustomerId, IReadOnlyList<OrderLine> Lines, string? Scenario = null);

public record OrderAccepted(string OrderId, string WorkflowId, bool RequiresApproval);

public record ApprovalRequest(string? ApprovedBy);

public record RejectionRequest(string Reason, string? RejectedBy);

public record OrderView(string OrderId, OrderStatus Status, string CustomerId, decimal TotalAmount, string? CancellationReason);

public record CreateOrderRecordRequest(string CustomerId, IReadOnlyList<OrderLine> Lines, decimal TotalAmount, string? Scenario);

public record CancelOrderRequest(string Reason);

public record ReserveInventoryRequest(IReadOnlyList<OrderLine> Lines);

public record ReservationView(string OrderId, string Status);

public record ChargeRequest(decimal Amount);

public record PaymentReceipt(string OrderId, string PaymentId, decimal Amount, string Status);

public record RefundResult(string OrderId, string Outcome);

public record RefundOutageRequest(bool Enabled);

public record BusinessError(string Code, string Message);

public record ServiceOutcome(string? ErrorCode = null, string? Message = null)
{
    public static readonly ServiceOutcome Ok = new();

    public bool Succeeded => ErrorCode is null;

    public static ServiceOutcome Fail(string code, string message) => new(code, message);

    public BusinessError ToError() => new(ErrorCode ?? "Unknown", Message ?? string.Empty);
}

public static class BusinessErrorCodes
{
    public const string OutOfStock = "OutOfStock";
    public const string UnknownSku = "UnknownSku";
    public const string PaymentDeclined = "PaymentDeclined";
    public const string PaymentVoided = "PaymentVoided";
    public const string OrderCannotBeConfirmed = "OrderCannotBeConfirmed";
    public const string InvalidOrderState = "InvalidOrderState";
    public const string OrderNotFound = "OrderNotFound";
    public const string OrderAlreadyCompleted = "OrderAlreadyCompleted";
    public const string ReservationNotFound = "ReservationNotFound";
    public const string ReservationReleased = "ReservationReleased";
    public const string ReservationFinalized = "ReservationFinalized";

    public static readonly string[] All =
    [
        OutOfStock,
        UnknownSku,
        PaymentDeclined,
        PaymentVoided,
        OrderCannotBeConfirmed,
        InvalidOrderState,
        OrderNotFound,
        OrderAlreadyCompleted,
        ReservationNotFound,
        ReservationReleased,
        ReservationFinalized,
    ];
}

public static class DemoScenarios
{
    public const string HeaderName = "X-Demo-Scenario";

    public const string TransientInventory = "TransientInventory";
    public const string PaymentDeclined = "PaymentDeclined";
    public const string PaymentTimeoutAfterCharge = "PaymentTimeoutAfterCharge";
    public const string ConfirmFails = "ConfirmFails";
}
