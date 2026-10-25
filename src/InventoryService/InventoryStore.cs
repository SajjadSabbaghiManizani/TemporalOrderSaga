using OrderSaga.Contracts;

namespace OrderSaga.InventoryService;

public sealed class InventoryStore
{
    private readonly object gate = new();

    private readonly Dictionary<string, StockLevel> stock = new(StringComparer.OrdinalIgnoreCase)
    {
        ["WIDGET"] = new StockLevel { OnHand = 50 },
        ["GADGET"] = new StockLevel { OnHand = 10 },
        ["GIZMO"] = new StockLevel { OnHand = 0 },
    };

    private readonly Dictionary<string, Reservation> reservations = new();
    private readonly Dictionary<string, int> attempts = new();

    public int CountAttempt(string orderId)
    {
        lock (gate)
        {
            attempts[orderId] = attempts.GetValueOrDefault(orderId) + 1;
            return attempts[orderId];
        }
    }

    public ServiceOutcome Reserve(string orderId, IReadOnlyList<OrderLine> lines)
    {
        lock (gate)
        {
            if (reservations.TryGetValue(orderId, out var existing))
            {
                return existing.Status == ReservationStatus.Released
                    ? ServiceOutcome.Fail(BusinessErrorCodes.ReservationReleased, $"Reservation for {orderId} was already released")
                    : ServiceOutcome.Ok;
            }

            var requested = lines
                .GroupBy(line => line.Sku, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Sum(line => line.Quantity), StringComparer.OrdinalIgnoreCase);

            foreach (var (sku, quantity) in requested)
            {
                if (!stock.TryGetValue(sku, out var level))
                {
                    return ServiceOutcome.Fail(BusinessErrorCodes.UnknownSku, $"Unknown SKU {sku}");
                }

                if (level.Available < quantity)
                {
                    return ServiceOutcome.Fail(BusinessErrorCodes.OutOfStock, $"Only {level.Available} of {sku} available, {quantity} requested");
                }
            }

            foreach (var (sku, quantity) in requested)
            {
                stock[sku].Reserved += quantity;
            }

            reservations[orderId] = new Reservation(requested);
            return ServiceOutcome.Ok;
        }
    }

    public ServiceOutcome Release(string orderId)
    {
        lock (gate)
        {
            if (!reservations.TryGetValue(orderId, out var reservation))
            {
                // A reserve request whose outcome the caller never saw may still be in flight. Recording the
                // release makes that late request fail instead of leaking a reservation.
                reservations[orderId] = new Reservation(new Dictionary<string, int>()) { Status = ReservationStatus.Released };
                return ServiceOutcome.Ok;
            }

            if (reservation.Status == ReservationStatus.Released)
            {
                return ServiceOutcome.Ok;
            }

            if (reservation.Status == ReservationStatus.Finalized)
            {
                return ServiceOutcome.Fail(BusinessErrorCodes.ReservationFinalized, $"Reservation for {orderId} is already finalized and cannot be released");
            }

            foreach (var (sku, quantity) in reservation.Quantities)
            {
                stock[sku].Reserved -= quantity;
            }

            reservation.Status = ReservationStatus.Released;
            return ServiceOutcome.Ok;
        }
    }

    public ServiceOutcome FinalizeReservation(string orderId)
    {
        lock (gate)
        {
            if (!reservations.TryGetValue(orderId, out var reservation))
            {
                return ServiceOutcome.Fail(BusinessErrorCodes.ReservationNotFound, $"No reservation for {orderId}");
            }

            switch (reservation.Status)
            {
                case ReservationStatus.Finalized:
                    return ServiceOutcome.Ok;
                case ReservationStatus.Released:
                    return ServiceOutcome.Fail(BusinessErrorCodes.ReservationReleased, $"Reservation for {orderId} was released");
            }

            foreach (var (sku, quantity) in reservation.Quantities)
            {
                stock[sku].Reserved -= quantity;
                stock[sku].OnHand -= quantity;
            }

            reservation.Status = ReservationStatus.Finalized;
            return ServiceOutcome.Ok;
        }
    }

    public ReservationView? FindReservation(string orderId)
    {
        lock (gate)
        {
            return reservations.TryGetValue(orderId, out var reservation)
                ? new ReservationView(orderId, reservation.Status.ToString())
                : null;
        }
    }

    public IReadOnlyList<StockView> Snapshot()
    {
        lock (gate)
        {
            return stock
                .Select(entry => new StockView(entry.Key, entry.Value.OnHand, entry.Value.Reserved, entry.Value.Available))
                .OrderBy(view => view.Sku)
                .ToList();
        }
    }

    private enum ReservationStatus
    {
        Reserved,
        Released,
        Finalized,
    }

    private sealed class StockLevel
    {
        public int OnHand { get; set; }

        public int Reserved { get; set; }

        public int Available => OnHand - Reserved;
    }

    private sealed class Reservation(Dictionary<string, int> quantities)
    {
        public Dictionary<string, int> Quantities { get; } = quantities;

        public ReservationStatus Status { get; set; } = ReservationStatus.Reserved;
    }
}

public record StockView(string Sku, int OnHand, int Reserved, int Available);
