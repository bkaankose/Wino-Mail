using System.Net;
using System.Text;
using Wino.Intelligence.ConsoleApp;
using Xunit;

namespace Wino.Intelligence.Console.Tests;

public sealed class StressHarnessTests
{
    [Fact]
    public void Defaults_AreAppliedForLocalRealisticRun()
    {
        var success = StressCommandLine.TryParse(
            ["--stress", "--account", "stress@example.test", "--output", "results"], out var options, out var error);

        Assert.True(success, error);
        Assert.NotNull(options);
        Assert.Equal(ApiEnvironment.Local, options.Environment);
        Assert.Equal(StressProfile.Realistic, options.Profile);
        Assert.Equal(1, options.StartRps);
        Assert.Equal(256, options.MaxRps);
        Assert.Equal(512, options.MaxConcurrency);
        Assert.Equal(TimeSpan.FromMinutes(5), options.StageDuration);
        Assert.Equal(TimeSpan.FromMinutes(60), options.SustainDuration);
    }

    [Fact]
    public void Production_RequiresExplicitConfirmation()
    {
        var success = StressCommandLine.TryParse(
            ["--stress", "--environment", "production", "--account", "stress@example.test", "--output", "results"],
            out _, out var error);

        Assert.False(success);
        Assert.Contains("--confirm-production-stress", error);
    }

    [Fact]
    public void AiProfile_RequiresPositiveRequestLimit()
    {
        var success = StressCommandLine.TryParse(
            ["--stress", "--profile", "ai", "--account", "stress@example.test", "--output", "results"],
            out _, out var error);

        Assert.False(success);
        Assert.Contains("--ai-request-limit", error);
    }

    [Fact]
    public void RealisticProfile_HasExactDeterministicWeights()
    {
        var operations = Enumerable.Range(0, 100).Select(x => StressWorkload.SelectOperation(StressProfile.Realistic, x));
        var counts = operations.GroupBy(x => x).ToDictionary(x => x.Key, x => x.Count());

        Assert.Equal(55, counts["search"]);
        Assert.Equal(10, counts["timeline"]);
        Assert.Equal(10, counts["delta"]);
        Assert.Equal(5, counts["artifacts"]);
        Assert.Equal(5, counts["ingest"]);
        Assert.Equal(15, counts["status"] + counts["manifest"]);
    }

    [Theory]
    [InlineData(0.50, 20)]
    [InlineData(0.95, 40)]
    [InlineData(0.99, 40)]
    public void Percentiles_UseNearestRank(double percentile, double expected)
    {
        Assert.Equal(expected, StressPercentiles.Calculate([10, 20, 30, 40], percentile));
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, 0)]
    [InlineData(HttpStatusCode.Unauthorized, 4)]
    [InlineData(HttpStatusCode.TooManyRequests, 6)]
    [InlineData(HttpStatusCode.PaymentRequired, 5)]
    [InlineData(HttpStatusCode.ServiceUnavailable, 1)]
    public void StatusCodes_AreClassified(HttpStatusCode status, int expected)
    {
        Assert.Equal(expected, (int)StressMeasurementHandler.Classify(status));
    }

    [Fact]
    public async Task MeasurementHandler_AddsRunIdAndRecordsOneAttemptWithoutRetry()
    {
        var inner = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("busy"),
            Headers = { RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(2)) },
        });
        using var client = new HttpClient(new StressMeasurementHandler(inner, "run-123"));
        var context = new StressOperationContext("search");

        using (StressOperationContext.Push(context))
            using (await client.GetAsync("https://example.test/api")) { }

        var attempt = Assert.Single(context.Attempts);
        Assert.Equal(StressFailureKind.Throttling, attempt.FailureKind);
        Assert.Equal("run-123", inner.LastRequest!.Headers.GetValues("X-Wino-Stress-Run-Id").Single());
        Assert.Equal(1, inner.CallCount);
    }

    [Fact]
    public async Task OpenLoopEngine_RecordsBacklogWhenConcurrencyIsSaturated()
    {
        var engine = new StressLoadEngine(1, "test-run");
        var snapshots = await engine.RunPhaseAsync("stub", 100, TimeSpan.FromMilliseconds(120), async (_, token) =>
        {
            var started = DateTimeOffset.UtcNow;
            await Task.Delay(80, token);
            return new StressOperationResult(started, "stub", "stub", 80, true, StressFailureKind.None, 200, 0, 0, null);
        }, CancellationToken.None);

        Assert.True(snapshots.Sum(x => x.Scheduled) > snapshots.Sum(x => x.Started));
        Assert.True(snapshots.Sum(x => x.Backlogged) > 0);
    }

    [Fact]
    public void MarkdownReport_MasksAccountAndContainsTelemetryGuidance()
    {
        var report = new StressReport("run", new Uri("https://api.example.test/private"), "secret@example.test",
            StressProfile.Realistic, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, StressStopReason.Completed,
            8, 16, 8, 0, []);

        var markdown = StressRunner.BuildMarkdown(report);

        Assert.DoesNotContain("secret@example.test", markdown);
        Assert.Contains("s***t@example.test", markdown);
        Assert.Contains("CPU credits", markdown);
        Assert.DoesNotContain("/private", markdown);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public HttpRequestMessage? LastRequest { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequest = request;
            return Task.FromResult(response(request));
        }
    }
}
