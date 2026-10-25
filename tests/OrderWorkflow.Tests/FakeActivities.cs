using System.Collections.Concurrent;
using OrderSaga.Contracts;
using OrderSaga.Worker.Activities;
using Temporalio.Activities;
using Temporalio.Exceptions;

namespace OrderSaga.Tests;

public sealed class FakeActivities
{
    private readonly ConcurrentQueue<string> calls = new();
    private int reserveAttempts;
    private int chargeAttempts;
    private int confirmAttempts;
    private int refundAttempts;

    public string[] Calls => calls.ToArray();

    public Func<int, Exception?> ReserveFailure { get; set; } = _ => null;

    public Func<int, Exception?> ChargeFailure { get; set; } = _ => null;

    public Func<int, Exception?> ConfirmFailure { get; set; } = _ => null;

    public Func<int, Exception?> RefundFailure { get; set; } = _ => null;

    public static Exception Rejected(string errorCode) =>
        new ApplicationFailureException($"Rejected: {errorCode}", errorType: errorCode);

    public static Exception Outage() => new HttpRequestException("Simulated dependency outage");

    [Activity("CreateOrder")]
    public Task CreateOrderAsync(CreateOrderCommand command)
    {
        calls.Enqueue("CreateOrder");
        return Task.CompletedTask;
    }

    [Activity("ReserveInventory")]
    public Task ReserveInventoryAsync(ReserveInventoryCommand command)
    {
        Record("ReserveInventory", ref reserveAttempts, ReserveFailure);
        return Task.CompletedTask;
    }

    [Activity("ChargePayment")]
    public Task<PaymentReceipt> ChargePaymentAsync(ChargePaymentCommand command)
    {
        Record("ChargePayment", ref chargeAttempts, ChargeFailure);
        return Task.FromResult(new PaymentReceipt(command.OrderId, "pay-test", command.Amount, "Charged"));
    }

    [Activity("ConfirmOrder")]
    public Task ConfirmOrderAsync(ConfirmOrderCommand command)
    {
        Record("ConfirmOrder", ref confirmAttempts, ConfirmFailure);
        return Task.CompletedTask;
    }

    [Activity("FinalizeInventory")]
    public Task FinalizeInventoryAsync(FinalizeInventoryCommand command)
    {
        calls.Enqueue("FinalizeInventory");
        return Task.CompletedTask;
    }

    [Activity("CompleteOrder")]
    public Task CompleteOrderAsync(CompleteOrderCommand command)
    {
        calls.Enqueue("CompleteOrder");
        return Task.CompletedTask;
    }

    [Activity("RefundPayment")]
    public Task<RefundResult> RefundPaymentAsync(RefundPaymentCommand command)
    {
        Record("RefundPayment", ref refundAttempts, RefundFailure);
        return Task.FromResult(new RefundResult(command.OrderId, "Refunded"));
    }

    [Activity("ReleaseInventory")]
    public Task ReleaseInventoryAsync(ReleaseInventoryCommand command)
    {
        calls.Enqueue("ReleaseInventory");
        return Task.CompletedTask;
    }

    [Activity("CancelOrder")]
    public Task CancelOrderAsync(CancelOrderCommand command)
    {
        calls.Enqueue("CancelOrder");
        return Task.CompletedTask;
    }

    private void Record(string name, ref int attempts, Func<int, Exception?> failure)
    {
        calls.Enqueue(name);
        var attempt = Interlocked.Increment(ref attempts);
        if (failure(attempt) is { } exception)
        {
            throw exception;
        }
    }
}
