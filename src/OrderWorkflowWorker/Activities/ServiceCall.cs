using System.Net;
using System.Net.Http.Json;
using OrderSaga.Contracts;
using Temporalio.Activities;
using Temporalio.Exceptions;

namespace OrderSaga.Worker.Activities;

internal static class ServiceCall
{
    public static async Task SendAsync(HttpClient http, HttpRequestMessage request, string? scenario = null)
    {
        using var response = await SendAndEnsureSuccessAsync(http, request, scenario);
    }

    public static async Task<T> SendAsync<T>(HttpClient http, HttpRequestMessage request, string? scenario = null)
    {
        using var response = await SendAndEnsureSuccessAsync(http, request, scenario);
        var body = await response.Content.ReadFromJsonAsync<T>(ActivityExecutionContext.Current.CancellationToken);
        return body ?? throw new ApplicationFailureException($"{request.Method} {request.RequestUri} returned an empty body", errorType: "EmptyResponse");
    }

    private static async Task<HttpResponseMessage> SendAndEnsureSuccessAsync(HttpClient http, HttpRequestMessage request, string? scenario)
    {
        if (!string.IsNullOrEmpty(scenario))
        {
            request.Headers.Add(DemoScenarios.HeaderName, scenario);
        }

        var cancellation = ActivityExecutionContext.Current.CancellationToken;
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellation);
        }
        catch (TaskCanceledException) when (!cancellation.IsCancellationRequested)
        {
            // HttpClient reports its own timeout as a cancellation. Rethrowing it as-is would look like
            // an activity cancellation to Temporal, so it is surfaced as an ordinary retryable failure.
            throw new ApplicationFailureException($"{request.Method} {request.RequestUri} timed out", errorType: "ServiceTimeout");
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        using (response)
        {
            throw await CreateFailureAsync(response, cancellation);
        }
    }

    private static async Task<Exception> CreateFailureAsync(HttpResponseMessage response, CancellationToken cancellation)
    {
        if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
        {
            var error = await response.Content.ReadFromJsonAsync<BusinessError>(cancellation);
            return new ApplicationFailureException(
                error?.Message ?? "A business rule rejected the request",
                errorType: error?.Code ?? "BusinessRuleViolation");
        }

        var status = (int)response.StatusCode;
        if (status is >= 400 and < 500 and not 408 and not 429)
        {
            return new ApplicationFailureException(
                $"Unexpected {status} from {response.RequestMessage?.RequestUri}; this is a bug in the caller, retrying will not help",
                errorType: "UnexpectedClientError",
                nonRetryable: true);
        }

        return new HttpRequestException($"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri} returned {status}", null, response.StatusCode);
    }
}
