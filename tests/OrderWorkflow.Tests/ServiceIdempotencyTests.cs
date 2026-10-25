using OrderSaga.Contracts;
using OrderSaga.InventoryService;
using OrderSaga.PaymentService;

namespace OrderSaga.Tests;

public class ServiceIdempotencyTests
{
    private static readonly OrderLine[] Lines = [new OrderLine("WIDGET", 2, 10m)];

    [Fact]
    public void Repeated_reserve_holds_stock_only_once()
    {
        var inventory = new InventoryStore();

        Assert.True(inventory.Reserve("o1", Lines).Succeeded);
        Assert.True(inventory.Reserve("o1", Lines).Succeeded);

        Assert.Equal(2, inventory.Snapshot().Single(stock => stock.Sku == "WIDGET").Reserved);
    }

    [Fact]
    public void Release_is_idempotent_and_returns_the_stock()
    {
        var inventory = new InventoryStore();
        inventory.Reserve("o1", Lines);

        Assert.True(inventory.Release("o1").Succeeded);
        Assert.True(inventory.Release("o1").Succeeded);

        Assert.Equal(0, inventory.Snapshot().Single(stock => stock.Sku == "WIDGET").Reserved);
    }

    [Fact]
    public void Reserve_that_arrives_after_its_release_is_refused_instead_of_leaking_stock()
    {
        var inventory = new InventoryStore();

        inventory.Release("o1");
        var late = inventory.Reserve("o1", Lines);

        Assert.Equal(BusinessErrorCodes.ReservationReleased, late.ErrorCode);
        Assert.Equal(0, inventory.Snapshot().Single(stock => stock.Sku == "WIDGET").Reserved);
    }

    [Fact]
    public void Finalized_reservation_cannot_be_released()
    {
        var inventory = new InventoryStore();
        inventory.Reserve("o1", Lines);
        inventory.FinalizeReservation("o1");

        Assert.Equal(BusinessErrorCodes.ReservationFinalized, inventory.Release("o1").ErrorCode);
    }

    [Fact]
    public void Reserving_more_than_available_is_rejected()
    {
        var inventory = new InventoryStore();

        var outcome = inventory.Reserve("o1", [new OrderLine("GADGET", 11, 5m)]);

        Assert.Equal(BusinessErrorCodes.OutOfStock, outcome.ErrorCode);
    }

    [Fact]
    public void Repeated_charge_returns_the_same_payment_without_charging_twice()
    {
        var payments = new PaymentStore();

        var first = payments.Charge("o1", 20m, decline: false);
        var second = payments.Charge("o1", 20m, decline: false);

        Assert.True(first.Created);
        Assert.False(second.Created);
        Assert.Equal(first.Payment!.PaymentId, second.Payment!.PaymentId);
    }

    [Fact]
    public void Refund_of_a_charged_payment_is_idempotent()
    {
        var payments = new PaymentStore();
        payments.Charge("o1", 20m, decline: false);

        Assert.Equal("Refunded", payments.Refund("o1"));
        Assert.Equal("AlreadyRefunded", payments.Refund("o1"));
    }

    [Fact]
    public void Charge_that_arrives_after_a_refund_with_nothing_to_refund_is_refused()
    {
        var payments = new PaymentStore();

        Assert.Equal("NothingToRefund", payments.Refund("o1"));
        var late = payments.Charge("o1", 20m, decline: false);

        Assert.Equal(BusinessErrorCodes.PaymentVoided, late.Outcome.ErrorCode);
    }

    [Fact]
    public void Declined_charge_records_nothing_to_refund()
    {
        var payments = new PaymentStore();

        var outcome = payments.Charge("o1", 20m, decline: true);

        Assert.Equal(BusinessErrorCodes.PaymentDeclined, outcome.Outcome.ErrorCode);
        Assert.Null(payments.Find("o1"));
    }
}
