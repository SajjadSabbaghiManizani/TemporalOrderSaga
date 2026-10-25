using OrderSaga.Contracts;

namespace OrderSaga.Worker.Activities;

public record CreateOrderCommand(string OrderId, string CustomerId, IReadOnlyList<OrderLine> Lines, decimal TotalAmount, string? Scenario);

public record ConfirmOrderCommand(string OrderId);

public record CompleteOrderCommand(string OrderId);

public record CancelOrderCommand(string OrderId, string Reason);

public record ReserveInventoryCommand(string OrderId, IReadOnlyList<OrderLine> Lines, string? Scenario);

public record ReleaseInventoryCommand(string OrderId);

public record FinalizeInventoryCommand(string OrderId);

public record ChargePaymentCommand(string OrderId, decimal Amount, string? Scenario);

public record RefundPaymentCommand(string OrderId);
