using Moq;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public sealed class SynchronizationNotificationCapabilityTests
{
    [Fact]
    public async Task SuccessfulProviderResult_WhenNotificationsUnavailable_DoesNotInvokeNativeNotificationsOrBadge()
    {
        var native = new Mock<INotificationBuilder>(MockBehavior.Strict);
        var result = new MailSynchronizationResult
        {
            CompletedState = SynchronizationCompletedState.Success,
            DownloadedMessages = new[] { new MailCopy() },
        };

        await SynchronizationManager.PublishMailSynchronizationNotificationsAsync(result, native.Object, false);

        Assert.Equal(SynchronizationCompletedState.Success, result.CompletedState);
        native.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SuccessfulProviderResult_WhenNotificationsAvailable_PreservesWindowsNotificationsAndBadge()
    {
        var native = new Mock<INotificationBuilder>(MockBehavior.Strict);
        var messages = new[] { new MailCopy() };
        native.Setup(value => value.CreateNotificationsAsync(messages)).Returns(Task.CompletedTask);
        native.Setup(value => value.UpdateTaskbarIconBadgeAsync()).Returns(Task.CompletedTask);
        var result = new MailSynchronizationResult { DownloadedMessages = messages };

        await SynchronizationManager.PublishMailSynchronizationNotificationsAsync(result, native.Object, true);

        native.Verify(value => value.CreateNotificationsAsync(messages), Times.Once);
        native.Verify(value => value.UpdateTaskbarIconBadgeAsync(), Times.Once);
        native.VerifyNoOtherCalls();
    }
}
