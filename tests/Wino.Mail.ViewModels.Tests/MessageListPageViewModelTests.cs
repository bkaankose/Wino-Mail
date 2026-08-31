using Moq;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Xunit;

namespace Wino.Mail.ViewModels.Tests;

public sealed class MessageListPageViewModelTests
{
    [Fact]
    public void SelectingFirstHoverAction_PersistsNone()
    {
        var preferences = new Mock<IPreferencesService>();
        preferences.SetupGet(static service => service.LeftHoverAction).Returns(MailOperation.Archive);

        var viewModel = new MessageListPageViewModel(
            preferences.Object,
            Mock.Of<IThumbnailService>(),
            Mock.Of<IStatePersistanceService>(),
            Mock.Of<IDialogServiceBase>());

        viewModel.LeftHoverActionIndex = 0;

        preferences.VerifySet(static service => service.LeftHoverAction = MailOperation.None, Times.Once);
    }
}
