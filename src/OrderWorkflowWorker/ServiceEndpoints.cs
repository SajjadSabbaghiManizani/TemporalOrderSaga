namespace OrderSaga.Worker;

public class ServiceEndpoints
{
    public string Order { get; set; } = "http://localhost:5001";

    public string Inventory { get; set; } = "http://localhost:5002";

    public string Payment { get; set; } = "http://localhost:5003";
}
