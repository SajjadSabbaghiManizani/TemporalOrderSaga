using OrderSaga.Contracts;
using OrderSaga.OrderService;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<OrderApiOptions>(builder.Configuration.GetSection("Orders"));
builder.Services.AddSingleton<OrderStore>();

var app = builder.Build();

var temporalSettings = app.Configuration.GetSection("Temporal").Get<TemporalSettings>() ?? new TemporalSettings();
var temporalClient = await TemporalConnector.ConnectAsync(
    temporalSettings,
    app.Services.GetRequiredService<ILoggerFactory>(),
    app.Lifetime.ApplicationStopping);

OrderEndpoints.Map(app, temporalClient, temporalSettings.TaskQueue);

app.Run();
