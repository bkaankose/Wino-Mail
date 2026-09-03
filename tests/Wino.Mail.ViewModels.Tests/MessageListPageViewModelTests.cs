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

    [Theory]
    [InlineData(MailHoverActionButtonSize.Small, 0)]
    [InlineData(MailHoverActionButtonSize.Medium, 1)]
    [InlineData(MailHoverActionButtonSize.Large, 2)]
    public void PersistedHoverActionButtonSize_InitializesSelectedIndex(
        MailHoverActionButtonSize persistedSize,
        int expectedIndex)
    {
        var preferences = new Mock<IPreferencesService>();
        preferences.SetupGet(static service => service.HoverActionButtonSize).Returns(persistedSize);

        var viewModel = CreateViewModel(preferences);

        Assert.Equal(expectedIndex, viewModel.SelectedHoverActionButtonSizeIndex);
    }

    [Theory]
    [InlineData(0, MailHoverActionButtonSize.Small)]
    [InlineData(1, MailHoverActionButtonSize.Medium)]
    [InlineData(2, MailHoverActionButtonSize.Large)]
    public void SelectingHoverActionButtonSize_PersistsPreference(
        int selectedIndex,
        MailHoverActionButtonSize expectedSize)
    {
        var preferences = new Mock<IPreferencesService>();
        var initialSize = selectedIndex == 0
            ? MailHoverActionButtonSize.Large
            : MailHoverActionButtonSize.Small;
        preferences.SetupGet(static service => service.HoverActionButtonSize).Returns(initialSize);
        var viewModel = CreateViewModel(preferences);

        viewModel.SelectedHoverActionButtonSizeIndex = selectedIndex;

        preferences.VerifySet(service => service.HoverActionButtonSize = expectedSize, Times.Once);
    }

    [Fact]
    public void InvalidPersistedHoverActionButtonSize_FallsBackToSmall()
    {
        var preferences = new Mock<IPreferencesService>();
        preferences.SetupGet(static service => service.HoverActionButtonSize).Returns((MailHoverActionButtonSize)int.MaxValue);

        var viewModel = CreateViewModel(preferences);

        Assert.Equal(0, viewModel.SelectedHoverActionButtonSizeIndex);
    }

    private static MessageListPageViewModel CreateViewModel(Mock<IPreferencesService> preferences) => new(
        preferences.Object,
        Mock.Of<IThumbnailService>(),
        Mock.Of<IStatePersistanceService>(),
        Mock.Of<IDialogServiceBase>());
}
