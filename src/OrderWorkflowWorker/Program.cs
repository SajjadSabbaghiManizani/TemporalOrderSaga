using Microsoft.Extensions.Options;
using OrderSaga.Contracts;
using OrderSaga.Worker;
using OrderSaga.Worker.Activities;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<TemporalSettings>(builder.Configuration.GetSection("Temporal"));
builder.Services.Configure<ServiceEndpoints>(builder.Configuration.GetSection("Services"));

builder.Services.AddHttpClient<OrderActivities>((services, client) =>
    ConfigureServiceClient(client, services.GetRequiredService<IOptions<ServiceEndpoints>>().Value.Order));
builder.Services.AddHttpClient<InventoryActivities>((services, client) =>
    ConfigureServiceClient(client, services.GetRequiredService<IOptions<ServiceEndpoints>>().Value.Inventory));
builder.Services.AddHttpClient<PaymentActivities>((services, client) =>
    ConfigureServiceClient(client, services.GetRequiredService<IOptions<ServiceEndpoints>>().Value.Payment));

builder.Services.AddSingleton<StockAuditActivities>();
builder.Services.AddHostedService<OrderWorker>();

await builder.Build().RunAsync();

static void ConfigureServiceClient(HttpClient client, string baseAddress)
{
    client.BaseAddress = new Uri(baseAddress);
    client.Timeout = TimeSpan.FromSeconds(5);
}
