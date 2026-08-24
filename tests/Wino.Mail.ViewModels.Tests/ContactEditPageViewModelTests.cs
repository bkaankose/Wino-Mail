using System;
using FluentAssertions;
using Moq;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Common;
using Wino.Core.Domain.Models.Contacts;
using Wino.Mail.ViewModels;
using Xunit;

namespace Wino.Mail.ViewModels.Tests;

public class ContactEditPageViewModelTests
{
    [Theory]
    [InlineData(2001, 2, 31, false)]
    [InlineData(2001, 4, 31, false)]
    [InlineData(2000, 2, 29, true)]
    [InlineData(2001, 2, 28, true)]
    public async Task Save_RejectsBirthdaysThatAreNotRealCalendarDates(int year, int month, int day, bool isValid)
    {
        var viewModel = new ContactEditPageViewModel(Mock.Of<IContactService>(), Mock.Of<IWinoRequestDelegator>(), Mock.Of<INavigationService>(), Mock.Of<IDialogServiceBase>(), Mock.Of<IContactPictureFileService>());
        var destination = new ContactCreateDestination(Guid.NewGuid(), Guid.NewGuid(), ContactSourceKind.Local, "Test", "Local contacts", true);
        viewModel.Destinations.Add(destination);
        viewModel.SelectedDestination = destination;
        viewModel.DisplayName = "Birthday Person";
        viewModel.BirthdayYear = year;
        viewModel.BirthdayMonth = month;
        viewModel.BirthdayDay = day;

        await viewModel.SaveCommand.ExecuteAsync(null);

        viewModel.IsErrorOpen.Should().Be(!isValid);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task BackCommand_DirtyEditor_OnlyNavigatesAfterDiscardConfirmation(bool confirmed, bool shouldNavigate)
    {
        var navigation = new Mock<INavigationService>();
        var dialogs = new Mock<IDialogServiceBase>();
        dialogs.Setup(service => service.ShowConfirmationDialogAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>()))
            .ReturnsAsync(confirmed);
        var viewModel = new ContactEditPageViewModel(
            Mock.Of<IContactService>(),
            Mock.Of<IWinoRequestDelegator>(),
            navigation.Object,
            dialogs.Object,
            Mock.Of<IContactPictureFileService>());
        viewModel.MarkDirty();

        await viewModel.BackCommand.ExecuteAsync(null);

        viewModel.IsBackNavigationApproved.Should().Be(shouldNavigate);
        navigation.Verify(service => service.GoBack(It.IsAny<Wino.Core.Domain.Enums.NavigationTransitionEffect>()),
            shouldNavigate ? Times.Once : Times.Never);
    }

    [Fact]
    public async Task ChooseAndRemovePhoto_UpdatesTheEditorPreviewImmediately()
    {
        byte[] imageBytes = [0x89, 0x50, 0x4E, 0x47];
        var dialogs = new Mock<IDialogServiceBase>();
        dialogs.Setup(service => service.PickFilesAsync(It.IsAny<object[]>()))
            .ReturnsAsync([new SharedFile("contact.png", imageBytes)]);
        var viewModel = new ContactEditPageViewModel(
            Mock.Of<IContactService>(),
            Mock.Of<IWinoRequestDelegator>(),
            Mock.Of<INavigationService>(),
            dialogs.Object,
            Mock.Of<IContactPictureFileService>());

        await viewModel.ChoosePhotoCommand.ExecuteAsync(null);

        viewModel.PreviewPhotoBytes.Should().Equal(imageBytes);
        viewModel.IsDirty.Should().BeTrue();

        viewModel.RemovePhotoCommand.Execute(null);

        viewModel.PreviewPhotoBytes.Should().BeNull();
        viewModel.PreviewPhotoPath.Should().BeNull();
    }

    [Fact]
    public async Task SaveWithPhoto_QueuesContactAndPhotoMutationsAsOneBatch()
    {
        byte[] imageBytes = [0x89, 0x50, 0x4E, 0x47];
        var pictureId = Guid.NewGuid();
        var dialogs = new Mock<IDialogServiceBase>();
        dialogs.Setup(service => service.PickFilesAsync(It.IsAny<object[]>()))
            .ReturnsAsync([new SharedFile("contact.png", imageBytes)]);
        var pictureService = new Mock<IContactPictureFileService>();
        pictureService.Setup(service => service.SaveContactPictureAsync(imageBytes)).ReturnsAsync(pictureId);
        var contactService = new Mock<IContactService>();
        contactService.Setup(service => service.SetContactFavoriteAsync(It.IsAny<Guid>(), It.IsAny<bool>())).Returns(Task.CompletedTask);
        contactService.Setup(service => service.SetListsForContactAsync(It.IsAny<Guid>(), It.IsAny<IEnumerable<Guid>>())).Returns(Task.CompletedTask);
        var delegator = new Mock<IWinoRequestDelegator>();
        var viewModel = new ContactEditPageViewModel(
            contactService.Object,
            delegator.Object,
            Mock.Of<INavigationService>(),
            dialogs.Object,
            pictureService.Object);
        var destination = new ContactCreateDestination(Guid.NewGuid(), Guid.NewGuid(), ContactSourceKind.Outlook, "Outlook", "Contacts", true);
        viewModel.Destinations.Add(destination);
        viewModel.SelectedDestination = destination;
        viewModel.DisplayName = "Photo Contact";
        viewModel.EmailAddresses.Add(new() { Address = "photo@example.com", IsPrimary = true });

        await viewModel.ChoosePhotoCommand.ExecuteAsync(null);
        await viewModel.SaveCommand.ExecuteAsync(null);

        delegator.Verify(service => service.ExecuteAsync(It.Is<IReadOnlyList<ContactOperationPreparationRequest>>(requests =>
            requests.Count == 2 &&
            requests[0].Operation == ContactSynchronizerOperation.Create &&
            requests[1].Operation == ContactSynchronizerOperation.SetPhoto &&
            requests[1].Photo == imageBytes)), Times.Once);
        delegator.Verify(service => service.ExecuteAsync(It.IsAny<ContactOperationPreparationRequest>()), Times.Never);
    }
}
