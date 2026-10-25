using OrderSaga.Contracts;

namespace OrderSaga.OrderService;

public sealed class OrderStore
{
    private readonly object gate = new();
    private readonly Dictionary<string, OrderRecord> orders = new();

    public ServiceOutcome Create(string orderId, CreateOrderRecordRequest request)
    {
        lock (gate)
        {
            if (!orders.ContainsKey(orderId))
            {
                orders[orderId] = new OrderRecord(orderId, request.CustomerId, request.TotalAmount, request.Scenario);
            }

            return ServiceOutcome.Ok;
        }
    }

    public ServiceOutcome Confirm(string orderId)
    {
        lock (gate)
        {
            if (!orders.TryGetValue(orderId, out var order))
            {
                return ServiceOutcome.Fail(BusinessErrorCodes.OrderNotFound, $"Order {orderId} does not exist");
            }

            if (order.Scenario == DemoScenarios.ConfirmFails)
            {
                return ServiceOutcome.Fail(BusinessErrorCodes.OrderCannotBeConfirmed, "Simulated: the order cannot be confirmed");
            }

            switch (order.Status)
            {
                case OrderStatus.Created:
                    order.Status = OrderStatus.Confirmed;
                    return ServiceOutcome.Ok;
                case OrderStatus.Confirmed:
                    return ServiceOutcome.Ok;
                default:
                    return InvalidState(order, "confirm");
            }
        }
    }

    public ServiceOutcome Complete(string orderId)
    {
        lock (gate)
        {
            if (!orders.TryGetValue(orderId, out var order))
            {
                return ServiceOutcome.Fail(BusinessErrorCodes.OrderNotFound, $"Order {orderId} does not exist");
            }

            switch (order.Status)
            {
                case OrderStatus.Confirmed:
                    order.Status = OrderStatus.Completed;
                    return ServiceOutcome.Ok;
                case OrderStatus.Completed:
                    return ServiceOutcome.Ok;
                default:
                    return InvalidState(order, "complete");
            }
        }
    }

    public ServiceOutcome Cancel(string orderId, string reason)
    {
        lock (gate)
        {
            if (!orders.TryGetValue(orderId, out var order))
            {
                return ServiceOutcome.Ok;
            }

            switch (order.Status)
            {
                case OrderStatus.Created:
                case OrderStatus.Confirmed:
                    order.Status = OrderStatus.Cancelled;
                    order.CancellationReason = reason;
                    return ServiceOutcome.Ok;
                case OrderStatus.Cancelled:
                    return ServiceOutcome.Ok;
                default:
                    return ServiceOutcome.Fail(BusinessErrorCodes.OrderAlreadyCompleted, $"Order {orderId} is already completed and cannot be cancelled");
            }
        }
    }

    public OrderView? Find(string orderId)
    {
        lock (gate)
        {
            return orders.TryGetValue(orderId, out var order) ? order.ToView() : null;
        }
    }

    public IReadOnlyList<OrderView> List()
    {
        lock (gate)
        {
            return orders.Values.Select(order => order.ToView()).ToList();
        }
    }

    private static ServiceOutcome InvalidState(OrderRecord order, string action) =>
        ServiceOutcome.Fail(BusinessErrorCodes.InvalidOrderState, $"Cannot {action} order {order.OrderId} while it is {order.Status}");

    private sealed class OrderRecord(string orderId, string customerId, decimal totalAmount, string? scenario)
    {
        public string OrderId { get; } = orderId;

        public string? Scenario { get; } = scenario;

        public OrderStatus Status { get; set; } = OrderStatus.Created;

        public string? CancellationReason { get; set; }

        public OrderView ToView() => new(OrderId, Status, customerId, totalAmount, CancellationReason);
    }
}
