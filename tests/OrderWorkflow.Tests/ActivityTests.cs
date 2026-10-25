using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OrderSaga.Contracts;
using OrderSaga.Worker.Activities;
using Temporalio.Converters;
using Temporalio.Exceptions;
using Temporalio.Testing;

namespace OrderSaga.Tests;

public class ActivityTests
{
    [Fact]
    public async Task Business_rejection_becomes_a_retryable_failure_carrying_the_service_error_code()
    {
        using var http = ClientReturning(HttpStatusCode.UnprocessableEntity, new BusinessError("PaymentDeclined", "Card declined"));

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(() => Send(http));

        Assert.Equal("PaymentDeclined", failure.ErrorType);
        Assert.False(failure.NonRetryable);
    }

    [Fact]
    public async Task Server_error_surfaces_as_an_ordinary_exception_so_temporal_retries_it()
    {
        using var http = ClientReturning(HttpStatusCode.ServiceUnavailable);

        await Assert.ThrowsAsync<HttpRequestException>(() => Send(http));
    }

    [Fact]
    public async Task Unexpected_client_error_is_not_retryable()
    {
        using var http = ClientReturning(HttpStatusCode.BadRequest);

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(() => Send(http));

        Assert.Equal("UnexpectedClientError", failure.ErrorType);
        Assert.True(failure.NonRetryable);
    }

    [Fact]
    public async Task Stock_audit_heartbeats_its_progress_after_every_batch()
    {
        var heartbeats = new List<object?>();
        var environment = new ActivityEnvironment { Heartbeater = details => heartbeats.Add(details.Single()) };

        var result = await environment.RunAsync(
            () => NewAudit().AuditStockAsync(new StockAuditRequest(3, TimeSpan.FromMilliseconds(1))));

        Assert.Equal<object?>([1, 2, 3], heartbeats);
        Assert.Equal(0, result.ResumedFromBatch);
    }

    [Fact]
    public async Task Stock_audit_resumes_after_the_last_recorded_batch_when_retried()
    {
        var heartbeats = new List<object?>();
        var recordedProgress = DataConverter.Default.PayloadConverter.ToPayload(3);
        var environment = new ActivityEnvironment
        {
            Info = ActivityEnvironment.DefaultInfo with { Attempt = 2, HeartbeatDetails = new[] { recordedProgress } },
            Heartbeater = details => heartbeats.Add(details.Single()),
        };

        var result = await environment.RunAsync(
            () => NewAudit().AuditStockAsync(new StockAuditRequest(5, TimeSpan.FromMilliseconds(1))));

        Assert.Equal(3, result.ResumedFromBatch);
        Assert.Equal(2, result.Attempt);
        Assert.Equal<object?>([4, 5], heartbeats);
    }

    [Fact]
    public async Task Stock_audit_ignores_recorded_progress_beyond_the_total()
    {
        var environment = new ActivityEnvironment
        {
            Info = ActivityEnvironment.DefaultInfo with
            {
                HeartbeatDetails = new[] { DataConverter.Default.PayloadConverter.ToPayload(99) },
            },
        };

        var result = await environment.RunAsync(
            () => NewAudit().AuditStockAsync(new StockAuditRequest(5, TimeSpan.FromMilliseconds(1))));

        Assert.Equal(5, result.ResumedFromBatch);
    }

    [Fact]
    public async Task Stock_audit_stops_promptly_when_cancelled()
    {
        var heartbeats = new List<object?>();
        ActivityEnvironment? environment = null;
        environment = new ActivityEnvironment
        {
            Heartbeater = details =>
            {
                heartbeats.Add(details.Single());
                if (heartbeats.Count == 2)
                {
                    environment!.Cancel();
                }
            },
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => environment.RunAsync(
            () => NewAudit().AuditStockAsync(new StockAuditRequest(10, TimeSpan.FromMilliseconds(5)))));

        Assert.Equal(2, heartbeats.Count);
    }

    private static StockAuditActivities NewAudit() => new(NullLogger<StockAuditActivities>.Instance);

    private static Task Send(HttpClient http) =>
        new ActivityEnvironment().RunAsync(
            () => ServiceCall.SendAsync(http, new HttpRequestMessage(HttpMethod.Put, "payments/ord-test")));

    private static HttpClient ClientReturning(HttpStatusCode status, object? body = null) =>
        new(new StubHandler(status, body)) { BaseAddress = new Uri("http://service.test") };

    private sealed class StubHandler(HttpStatusCode status, object? body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(status);
            if (body is not null)
            {
                response.Content = JsonContent.Create(body, body.GetType());
            }

            return Task.FromResult(response);
        }
    }
}
