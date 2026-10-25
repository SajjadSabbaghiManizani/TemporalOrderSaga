using OrderSaga.Contracts;
using Temporalio.Common;
using Temporalio.Workflows;

namespace OrderSaga.Worker.Workflows;

internal static class ActivityProfiles
{
    // Steps that can still be undone. Bounded in time and attempts: if the dependency stays unhealthy,
    // the saga gives up and compensates instead of holding resources indefinitely.
    public static ActivityOptions Forward() => new()
    {
        StartToCloseTimeout = TimeSpan.FromSeconds(10),
        ScheduleToCloseTimeout = TimeSpan.FromMinutes(1),
        RetryPolicy = new RetryPolicy
        {
            InitialInterval = TimeSpan.FromSeconds(1),
            BackoffCoefficient = 2,
            MaximumInterval = TimeSpan.FromSeconds(10),
            MaximumAttempts = 5,
            NonRetryableErrorTypes = BusinessErrorCodes.All,
        },
    };

    // Steps after the order is confirmed. Retried until they succeed and shielded from workflow
    // cancellation, because stopping half way would leave a confirmed order that is not finished.
    public static ActivityOptions PointOfNoReturn() => new()
    {
        StartToCloseTimeout = TimeSpan.FromSeconds(10),
        CancellationToken = CancellationToken.None,
        RetryPolicy = new RetryPolicy
        {
            InitialInterval = TimeSpan.FromSeconds(1),
            BackoffCoefficient = 2,
            MaximumInterval = TimeSpan.FromSeconds(30),
            MaximumAttempts = 0,
            NonRetryableErrorTypes = BusinessErrorCodes.All,
        },
    };

    // Undo actions. Never given up on for transient errors, and shielded from workflow cancellation so a
    // cancelled order still releases what it holds. Errors that can never succeed on retry are listed as
    // non-retryable so they surface for a human instead of looping forever.
    public static ActivityOptions Compensation() => new()
    {
        StartToCloseTimeout = TimeSpan.FromSeconds(10),
        CancellationToken = CancellationToken.None,
        RetryPolicy = new RetryPolicy
        {
            InitialInterval = TimeSpan.FromSeconds(1),
            BackoffCoefficient = 2,
            MaximumInterval = TimeSpan.FromSeconds(10),
            MaximumAttempts = 0,
            NonRetryableErrorTypes = new[]
            {
                BusinessErrorCodes.OrderAlreadyCompleted,
                BusinessErrorCodes.ReservationFinalized,
            },
        },
    };

    public static ActivityOptions HeartbeatingJob() => new()
    {
        StartToCloseTimeout = TimeSpan.FromMinutes(10),
        HeartbeatTimeout = TimeSpan.FromSeconds(5),
        RetryPolicy = new RetryPolicy
        {
            InitialInterval = TimeSpan.FromSeconds(1),
            MaximumInterval = TimeSpan.FromSeconds(5),
        },
    };
}
