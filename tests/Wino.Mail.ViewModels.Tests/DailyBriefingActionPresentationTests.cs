using System;
using System.Linq;
using FluentAssertions;
using Wino.Mail.AI.Abstractions;
using Wino.Mail.ViewModels;
using Xunit;

namespace Wino.Mail.ViewModels.Tests;

public sealed class DailyBriefingActionPresentationTests
{
    [Fact]
    public void EveryConcreteActionPayload_HasAnExplicitPresentation()
    {
        var payloadTypes = typeof(BriefingActionPayload).Assembly.GetTypes()
            .Where(type => !type.IsAbstract && typeof(BriefingActionPayload).IsAssignableFrom(type))
            .OrderBy(type => type.Name)
            .ToArray();

        payloadTypes.Should().HaveCount(36);
        foreach (var payloadType in payloadTypes)
        {
            var payload = (BriefingActionPayload)Activator.CreateInstance(payloadType)!;
            var presentation = DailyBriefingActionPresentationFactory.Create(payload);

            if (payload is NoActionPayload)
            {
                presentation.Should().BeSameAs(DailyBriefingActionPresentation.None);
                continue;
            }

            presentation.Label.Should().NotBeNullOrWhiteSpace(payloadType.Name);
            presentation.Glyph.Should().NotBeNullOrWhiteSpace(payloadType.Name);
            presentation.AutomationId.Should().NotBe("DailyBriefingOpenFallbackButton", payloadType.Name);
            presentation.IsVisible.Should().BeTrue(payloadType.Name);
        }
    }

    [Theory]
    [InlineData(typeof(ReplyActionPayload), DailyBriefingActionExecution.Reply)]
    [InlineData(typeof(CopyVerificationCodeActionPayload), DailyBriefingActionExecution.CopyVerificationCode)]
    [InlineData(typeof(AddToCalendarActionPayload), DailyBriefingActionExecution.AddToCalendar)]
    [InlineData(typeof(PayActionPayload), DailyBriefingActionExecution.OpenSource)]
    public void ActionPayload_UsesExpectedExecutionRoute(Type payloadType, DailyBriefingActionExecution expected)
    {
        var payload = (BriefingActionPayload)Activator.CreateInstance(payloadType)!;

        DailyBriefingActionPresentationFactory.Create(payload).Execution.Should().Be(expected);
    }

    [Fact]
    public void NativeActionWithoutRequiredData_FallsBackToSourceMail()
    {
        DailyBriefingActionPresentationFactory.Create(new AddToCalendarActionPayload(), canAddToCalendar: false)
            .Execution.Should().Be(DailyBriefingActionExecution.OpenSource);
        DailyBriefingActionPresentationFactory.Create(new CopyVerificationCodeActionPayload { Code = string.Empty }, hasVerificationCode: false)
            .Execution.Should().Be(DailyBriefingActionExecution.OpenSource);
    }
}
