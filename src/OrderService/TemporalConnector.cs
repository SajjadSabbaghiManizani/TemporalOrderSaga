using OrderSaga.Contracts;
using Temporalio.Client;

namespace OrderSaga.OrderService;

public static class TemporalConnector
{
    private const int MaxAttempts = 30;

    public static async Task<ITemporalClient> ConnectAsync(TemporalSettings settings, ILoggerFactory loggerFactory, CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger(nameof(TemporalConnector));

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await TemporalClient.ConnectAsync(new TemporalClientConnectOptions(settings.Address)
                {
                    Namespace = settings.Namespace,
                    LoggerFactory = loggerFactory,
                });
            }
            catch (Exception exception) when (attempt < MaxAttempts && !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(exception, "Temporal is not reachable at {Address} (attempt {Attempt}/{MaxAttempts}), retrying", settings.Address, attempt, MaxAttempts);
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
        }
    }
}
