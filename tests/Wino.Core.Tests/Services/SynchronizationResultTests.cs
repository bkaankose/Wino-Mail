using FluentAssertions;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public class SynchronizationResultTests
{
    [Fact]
    public void Mail_result_merge_issues_should_mark_success_as_partial_and_set_exception()
    {
        var result = MailSynchronizationResult.Completed([]);
        var issues = new[]
        {
            new SynchronizationIssue
            {
                Message = "Create event failed",
                OperationType = "RequestExecution",
                Severity = SynchronizerErrorSeverity.Fatal
            }
        };

        result.MergeIssues(issues);

        result.CompletedState.Should().Be(SynchronizationCompletedState.PartiallyCompleted);
        result.Issues.Should().ContainSingle();
        result.Exception.Should().NotBeNull();
        result.Exception!.Message.Should().Be("Create event failed");
    }

    [Fact]
    public void Calendar_result_merge_issues_should_mark_success_as_partial_and_preserve_issue()
    {
        var result = CalendarSynchronizationResult.Empty;
        var issues = new[]
        {
            new SynchronizationIssue
            {
                Message = "Calendar API rate limit",
                OperationType = "CalendarSync",
                Severity = SynchronizerErrorSeverity.Transient
            }
        };

        result.MergeIssues(issues);

        result.CompletedState.Should().Be(SynchronizationCompletedState.PartiallyCompleted);
        result.Issues.Should().ContainSingle(issue => issue.Message == "Calendar API rate limit");
        result.Exception.Should().NotBeNull();
        result.Exception!.Message.Should().Be("Calendar API rate limit");
    }

    [Fact]
    public async Task Error_factory_should_record_handled_metadata_on_context()
    {
        var factory = new SynchronizerErrorHandlingFactory();
        factory.RegisterHandler(new TestErrorHandler());

        var context = new SynchronizerErrorContext
        {
            ErrorMessage = "Handled sync error"
        };

        var handled = await factory.HandleErrorAsync(context);

        handled.Should().BeTrue();
        context.WasHandled.Should().BeTrue();
        context.HandledBy.Should().Be(nameof(TestErrorHandler));
    }

    private sealed class TestErrorHandler : ISynchronizerErrorHandler
    {
        public bool CanHandle(SynchronizerErrorContext error) => true;

        public Task<bool> HandleAsync(SynchronizerErrorContext error) => Task.FromResult(true);
    }
}
