using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Synchronization;
using Wino.SmokeTest.ConsoleApp;
using Xunit;
using System.Reflection;

namespace Wino.SmokeTest.Console.Tests;

public sealed class SmokeConsoleTests
{
    [Fact]
    public void CommandLine_ParsesRequiredAccountAndOptionalValues()
    {
        var success = SmokeCommandLine.TryParse(
        [
            "--smoke",
            "--account", "user@example.test",
            "--report-to", "reports@example.test",
            "--attachments-folder", "."
        ], out var options, out var error);

        Assert.True(success, error);
        Assert.Equal("user@example.test", options.AccountAddress);
        Assert.Equal("reports@example.test", options.ReportRecipient);
        Assert.Equal(Path.GetFullPath("."), options.AttachmentsFolder);
    }

    [Fact]
    public void CommandLine_RequiresAccount()
    {
        var success = SmokeCommandLine.TryParse(["--smoke"], out _, out var error);

        Assert.False(success);
        Assert.Contains("--account", error);
    }

    [Fact]
    public void CommandLine_RejectsUnknownArguments()
    {
        var success = SmokeCommandLine.TryParse(
            ["--smoke", "--account", "user@example.test", "--unknown"],
            out _,
            out var error);

        Assert.False(success);
        Assert.Contains("--unknown", error);
    }

    [Theory]
    [InlineData("1", MailOperation.MarkAsRead)]
    [InlineData("2", MailOperation.MarkAsUnread)]
    [InlineData("5", MailOperation.Archive)]
    [InlineData("7", MailOperation.MoveToJunk)]
    [InlineData("10", MailOperation.HardDelete)]
    public void ManualOperation_MapsMenuSelection(string input, MailOperation expected)
    {
        var success = SmokeTestRunner.TryMapManualOperation(input, out var operation);

        Assert.True(success);
        Assert.Equal(expected, operation);
    }

    [Fact]
    public void ReportBuilder_EncodesHtmlAndIncludesStepFindings()
    {
        var result = CreateResult();
        result.Steps.Add(new SmokeStepResult(
            "Mail <sync>",
            SmokeStepStatus.Failed,
            TimeSpan.FromSeconds(2),
            "Failure & details",
            new Dictionary<string, int> { ["arrived"] = 1 }));

        var text = SmokeReportBuilder.BuildText(result);
        var html = SmokeReportBuilder.BuildHtml(result);

        Assert.Contains("[Failed] Mail <sync>", text);
        Assert.Contains("arrived: 1", text);
        Assert.Contains("Mail &lt;sync&gt;", html);
        Assert.Contains("Failure &amp; details", html);
        Assert.DoesNotContain("<sync>", html);
    }

    [Fact]
    public void RunResult_FailsForStepCleanupOrReportFailure()
    {
        var result = CreateResult();
        result.FixtureCleanupSucceeded = true;
        result.ReportSent = true;
        Assert.False(result.HasFailures);

        result.Steps.Add(new SmokeStepResult(
            "Failure",
            SmokeStepStatus.Failed,
            TimeSpan.Zero,
            "test"));
        Assert.True(result.HasFailures);
    }

    [Fact]
    public void ResultGuard_RejectsPartialSynchronization()
    {
        Assert.Throws<InvalidOperationException>(() => SmokeResultGuard.ThrowIfFailed(
            "Mail",
            SynchronizationCompletedState.PartiallyCompleted,
            null));
    }

    [Fact]
    public async Task SynchronizationHost_RoutesMessengerRequestAndReturnsResult()
    {
        var manager = SynchronizationManagerProxy.Create(async options =>
        {
            await Task.Yield();
            return MailSynchronizationResult.Empty;
        });
        using var host = new SmokeSynchronizationHost(manager);

        var result = await host.SynchronizeMailAsync(new MailSynchronizationOptions
        {
            AccountId = Guid.NewGuid(),
            Type = MailSynchronizationType.FullFolders
        }, CancellationToken.None);

        Assert.Equal(SynchronizationCompletedState.Success, result.CompletedState);
    }

    [Fact]
    public async Task SynchronizationHost_SerializesRequestsForTheSameAccount()
    {
        var active = 0;
        var maximumActive = 0;
        var manager = SynchronizationManagerProxy.Create(async _ =>
        {
            var current = Interlocked.Increment(ref active);
            maximumActive = Math.Max(maximumActive, current);
            await Task.Delay(25);
            Interlocked.Decrement(ref active);
            return MailSynchronizationResult.Empty;
        });
        using var host = new SmokeSynchronizationHost(manager);
        var accountId = Guid.NewGuid();

        await Task.WhenAll(
            host.SynchronizeMailAsync(new MailSynchronizationOptions
            {
                AccountId = accountId,
                Type = MailSynchronizationType.FullFolders
            }, CancellationToken.None),
            host.SynchronizeMailAsync(new MailSynchronizationOptions
            {
                AccountId = accountId,
                Type = MailSynchronizationType.FullFolders
            }, CancellationToken.None));

        Assert.Equal(1, maximumActive);
    }

    private static SmokeRunResult CreateResult()
        => new()
        {
            RunId = "run-1",
            StartedAtUtc = DateTimeOffset.Parse("2026-08-24T12:00:00Z"),
            AccountId = Guid.NewGuid(),
            AccountAddress = "user@example.test",
            Provider = "IMAP4"
        };
}

internal class SynchronizationManagerProxy : DispatchProxy
{
    private Func<MailSynchronizationOptions, Task<MailSynchronizationResult>> _mailHandler = null!;

    public static ISynchronizationManager Create(
        Func<MailSynchronizationOptions, Task<MailSynchronizationResult>> mailHandler)
    {
        var manager = Create<ISynchronizationManager, SynchronizationManagerProxy>();
        ((SynchronizationManagerProxy)(object)manager)._mailHandler = mailHandler;
        return manager;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        args ??= [];

        if (targetMethod.Name == nameof(ISynchronizationManager.SynchronizeMailAsync))
            return _mailHandler((MailSynchronizationOptions)args[0]!);

        throw new NotSupportedException(targetMethod.Name);
    }
}
