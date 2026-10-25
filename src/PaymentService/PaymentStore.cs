using OrderSaga.Contracts;

namespace OrderSaga.PaymentService;

public enum PaymentStatus
{
    Charged,
    Refunded,
    Voided,
}

public sealed class Payment(string orderId, string paymentId, decimal amount, PaymentStatus status)
{
    public string OrderId { get; } = orderId;

    public string PaymentId { get; } = paymentId;

    public decimal Amount { get; } = amount;

    public PaymentStatus Status { get; set; } = status;

    public PaymentReceipt ToReceipt() => new(OrderId, PaymentId, Amount, Status.ToString());
}

public sealed class PaymentStore
{
    private readonly object gate = new();
    private readonly Dictionary<string, Payment> payments = new();

    public bool RefundOutage { get; set; }

    public (ServiceOutcome Outcome, Payment? Payment, bool Created) Charge(string orderId, decimal amount, bool decline)
    {
        lock (gate)
        {
            if (payments.TryGetValue(orderId, out var existing))
            {
                if (existing.Status == PaymentStatus.Voided)
                {
                    return (ServiceOutcome.Fail(BusinessErrorCodes.PaymentVoided, $"Payment for {orderId} was voided by a refund request"), null, false);
                }

                return (ServiceOutcome.Ok, existing, false);
            }

            if (decline)
            {
                return (ServiceOutcome.Fail(BusinessErrorCodes.PaymentDeclined, "Simulated: the card was declined"), null, false);
            }

            var payment = new Payment(orderId, $"pay-{Guid.NewGuid():N}"[..12], amount, PaymentStatus.Charged);
            payments[orderId] = payment;
            return (ServiceOutcome.Ok, payment, true);
        }
    }

    public string Refund(string orderId)
    {
        lock (gate)
        {
            if (!payments.TryGetValue(orderId, out var payment))
            {
                payments[orderId] = new Payment(orderId, "voided", 0, PaymentStatus.Voided);
                return "NothingToRefund";
            }

            switch (payment.Status)
            {
                case PaymentStatus.Charged:
                    payment.Status = PaymentStatus.Refunded;
                    return "Refunded";
                case PaymentStatus.Refunded:
                    return "AlreadyRefunded";
                default:
                    return "AlreadyVoided";
            }
        }
    }

    public Payment? Find(string orderId)
    {
        lock (gate)
        {
            return payments.GetValueOrDefault(orderId);
        }
    }
}
