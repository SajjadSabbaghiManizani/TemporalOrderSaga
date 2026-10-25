namespace OrderSaga.OrderService;

public class OrderApiOptions
{
    public decimal ApprovalThreshold { get; set; } = 500m;

    public TimeSpan ApprovalTimeout { get; set; } = TimeSpan.FromDays(1);
}
