using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json.Serialization;

namespace Wino.SmokeTest.ConsoleApp;

internal enum StressProfile { Realistic, Database, Ai }
internal enum StressFailureKind { None, Http, Timeout, Transport, Authentication, Quota, Throttling, Validation }
internal enum StressStopReason { Completed, Cancelled, ErrorThreshold, ConcurrencySaturation, AuthenticationFailure, AiRequestLimit }

internal sealed record StressOptions(
    ApiEnvironment Environment,
    string Account,
    StressProfile Profile,
    double StartRps,
    double MaxRps,
    int MaxConcurrency,
    TimeSpan StageDuration,
    TimeSpan SustainDuration,
    int? AiRequestLimit,
    string OutputFolder,
    bool ConfirmProductionStress);

internal sealed record StressAttempt(
    string Route,
    int? StatusCode,
    double DurationMs,
    long RequestBytes,
    long ResponseBytes,
    string? RetryAfter,
    StressFailureKind FailureKind);

internal sealed record StressOperationResult(
    DateTimeOffset StartedAtUtc,
    string Phase,
    string Route,
    double DurationMs,
    bool Success,
    StressFailureKind FailureKind,
    int? StatusCode,
    long RequestBytes,
    long ResponseBytes,
    string? RetryAfter,
    IReadOnlyList<int>? AttemptStatusCodes = null,
    int AttemptCount = 0);

internal sealed record StressIntervalSnapshot(
    string RunId,
    string Phase,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset EndedAtUtc,
    double TargetRps,
    long Scheduled,
    long Started,
    long Completed,
    long Successful,
    long Backlogged,
    int InFlight,
    double ActualRps,
    double SuccessfulRps,
    double FailurePercentage,
    double P50Ms,
    double P90Ms,
    double P95Ms,
    double P99Ms,
    double MaximumMs,
    long RequestBytes,
    long ResponseBytes,
    IReadOnlyDictionary<string, long> StatusCodes,
    long Attempts,
    long RetryAfterResponses,
    IReadOnlyDictionary<string, long> Failures,
    IReadOnlyDictionary<string, StressRouteSnapshot> Routes)
{
    [JsonIgnore]
    public bool IsHealthy => Completed > 0 && FailurePercentage < 1d && P95Ms < 2_000d && Backlogged == 0;
}

internal sealed record StressRouteSnapshot(
    long Completed,
    long Successful,
    double P50Ms,
    double P95Ms,
    double P99Ms,
    double MaximumMs,
    IReadOnlyDictionary<string, long> StatusCodes,
    IReadOnlyDictionary<string, long> Failures);

internal sealed record StressPhaseSummary(
    string Name,
    double TargetRps,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset EndedAtUtc,
    bool Healthy,
    long Completed,
    long Successful,
    long Backlogged,
    double P95Ms,
    double FailurePercentage);

internal sealed record StressReport(
    string RunId,
    Uri Target,
    string Account,
    StressProfile Profile,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset EndedAtUtc,
    StressStopReason StopReason,
    double? LastHealthyRps,
    double? FirstUnhealthyRps,
    double? SustainableRps,
    int AiRequests,
    IReadOnlyList<StressPhaseSummary> Phases);

internal static class StressPercentiles
{
    public static double Calculate(IReadOnlyList<double> sortedValues, double percentile)
    {
        if (sortedValues.Count == 0) return 0;
        var rank = Math.Clamp((int)Math.Ceiling(percentile * sortedValues.Count) - 1, 0, sortedValues.Count - 1);
        return sortedValues[rank];
    }
}

internal sealed class StressOperationContext
{
    private static readonly AsyncLocal<StressOperationContext?> CurrentHolder = new();
    private readonly ConcurrentQueue<StressAttempt> _attempts = new();

    public StressOperationContext(string route) => Route = route;
    public string Route { get; }
    public static StressOperationContext? Current => CurrentHolder.Value;
    public IReadOnlyList<StressAttempt> Attempts => _attempts.ToArray();
    public static IDisposable Push(StressOperationContext context)
    {
        var previous = CurrentHolder.Value;
        CurrentHolder.Value = context;
        return new Scope(() => CurrentHolder.Value = previous);
    }
    public void Add(StressAttempt attempt) => _attempts.Enqueue(attempt);

    private sealed class Scope(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}

internal sealed class StressMeasurementHandler(HttpMessageHandler innerHandler, string runId) : DelegatingHandler(innerHandler)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.TryAddWithoutValidation("X-Wino-Stress-Run-Id", runId);
        var context = StressOperationContext.Current;
        var route = context?.Route ?? request.RequestUri?.AbsolutePath ?? "unknown";
        var requestBytes = request.Content?.Headers.ContentLength ?? 0;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            var failure = Classify(response.StatusCode);
            context?.Add(new StressAttempt(route, (int)response.StatusCode, stopwatch.Elapsed.TotalMilliseconds,
                requestBytes, response.Content.Headers.ContentLength ?? 0,
                response.Headers.RetryAfter?.ToString(), failure));
            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            context?.Add(new StressAttempt(route, null, stopwatch.Elapsed.TotalMilliseconds, requestBytes, 0, null,
                StressFailureKind.Timeout));
            throw;
        }
        catch (HttpRequestException)
        {
            stopwatch.Stop();
            context?.Add(new StressAttempt(route, null, stopwatch.Elapsed.TotalMilliseconds, requestBytes, 0, null,
                StressFailureKind.Transport));
            throw;
        }
    }

    internal static StressFailureKind Classify(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => StressFailureKind.Authentication,
        HttpStatusCode.TooManyRequests => StressFailureKind.Throttling,
        HttpStatusCode.PaymentRequired => StressFailureKind.Quota,
        >= HttpStatusCode.BadRequest => StressFailureKind.Http,
        _ => StressFailureKind.None,
    };
}
