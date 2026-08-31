using System;
using FluentAssertions;
using Moq;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Common;
using Wino.Core.Domain.Models.Contacts;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Requests.Contact;
using Wino.Mail.ViewModels;
using Xunit;

namespace Wino.Mail.ViewModels.Tests;

public class ContactEditPageViewModelTests
{
    [Fact]
    public async Task OnNavigatedTo_ResetsTheEditorToContactInformation()
    {
        var destination = new ContactCreateDestination(
            Guid.NewGuid(),
            Guid.NewGuid(),
            ContactSourceKind.Local,
            "Test",
            "Local contacts",
            true);
        var contactService = new Mock<IContactService>();
        contactService.Setup(service => service.GetCreateDestinationsAsync()).ReturnsAsync([destination]);
        contactService.Setup(service => service.GetContactListsAsync()).ReturnsAsync([]);
        var viewModel = new ContactEditPageViewModel(
            contactService.Object,
            Mock.Of<IWinoRequestDelegator>(),
            Mock.Of<INavigationService>(),
            Mock.Of<IMailDialogService>(),
            Mock.Of<IContactPictureFileService>())
        {
            SelectedCategory = ContactEditorCategory.Notes
        };

        viewModel.OnNavigatedTo(NavigationMode.New, new ContactEditNavigationParameter());

        await WaitForAsync(() => viewModel.SelectedDestination == destination);

        viewModel.SelectedCategory.Should().Be(ContactEditorCategory.ContactInformation);
    }

    [Theory]
    [InlineData("destination", ContactEditorCategory.ContactInformation)]
    [InlineData("display", ContactEditorCategory.ContactInformation)]
    [InlineData("invalid-email", ContactEditorCategory.ContactInformation)]
    [InlineData("duplicate-email", ContactEditorCategory.ContactInformation)]
    [InlineData("birthday", ContactEditorCategory.Other)]
    public async Task Save_ValidationError_SelectsTheCategoryContainingTheInvalidField(
        string errorCase,
        ContactEditorCategory expectedCategory)
    {
        var viewModel = CreateViewModel();
        var destination = new ContactCreateDestination(
            Guid.NewGuid(),
            Guid.NewGuid(),
            ContactSourceKind.Local,
            "Test",
            "Local contacts",
            true);
        viewModel.Destinations.Add(destination);
        viewModel.SelectedDestination = errorCase == "destination" ? null : destination;
        viewModel.DisplayName = errorCase == "display" ? null : "Validation Test";
        viewModel.SelectedCategory = ContactEditorCategory.Notes;

        if (errorCase == "invalid-email")
            viewModel.EmailAddresses.Add(new ContactEmailAddress { Address = "not-an-email" });
        else if (errorCase == "duplicate-email")
        {
            viewModel.EmailAddresses.Add(new ContactEmailAddress { Address = "person@example.com" });
            viewModel.EmailAddresses.Add(new ContactEmailAddress { Address = "PERSON@example.com" });
        }
        else if (errorCase == "birthday")
        {
            viewModel.BirthdayYear = 2001;
            viewModel.BirthdayMonth = 2;
            viewModel.BirthdayDay = 29;
        }

        await viewModel.SaveCommand.ExecuteAsync(null);

        viewModel.IsErrorOpen.Should().BeTrue();
        viewModel.SelectedCategory.Should().Be(expectedCategory);
    }

    [Fact]
    public async Task Save_ProviderFailureKeepsTheCurrentCategory()
    {
        var delegator = new Mock<IWinoRequestDelegator>();
        delegator.Setup(service => service.ExecuteAsync(It.IsAny<IReadOnlyList<ContactOperationPreparationRequest>>()))
            .ThrowsAsync(new InvalidOperationException("Provider rejected the contact."));
        var navigation = new Mock<INavigationService>();
        var viewModel = new ContactEditPageViewModel(
            Mock.Of<IContactService>(),
            delegator.Object,
            navigation.Object,
            Mock.Of<IMailDialogService>(),
            Mock.Of<IContactPictureFileService>());
        var destination = new ContactCreateDestination(
            Guid.NewGuid(),
            Guid.NewGuid(),
            ContactSourceKind.Outlook,
            "Outlook",
            "Contacts",
            true);
        viewModel.Destinations.Add(destination);
        viewModel.SelectedDestination = destination;
        viewModel.DisplayName = "Provider Failure";
        viewModel.SelectedCategory = ContactEditorCategory.Work;

        await viewModel.SaveCommand.ExecuteAsync(null);

        viewModel.IsErrorOpen.Should().BeTrue();
        viewModel.SelectedCategory.Should().Be(ContactEditorCategory.Work);
        navigation.Verify(service => service.SetNavigationResult(It.IsAny<NavigationResult>()), Times.Never);
        navigation.Verify(service => service.GoBack(), Times.Never);
    }

    [Fact]
    public async Task ToggleFavorite_EditMode_PersistsLocallyEvenForReadOnlyContacts()
    {
        var accountId = Guid.NewGuid();
        var addressBookId = Guid.NewGuid();
        var contact = new AccountContact
        {
            Id = Guid.NewGuid(),
            MailAccountId = accountId,
            AddressBookId = addressBookId,
            SourceKind = ContactSourceKind.CardDav,
            DisplayName = "Read-only favorite"
        };
        var destination = new ContactCreateDestination(
            accountId,
            addressBookId,
            ContactSourceKind.CardDav,
            "Work",
            "Shared contacts",
            false,
            IsReadOnly: true);
        var contactService = new Mock<IContactService>();
        contactService.Setup(service => service.GetCreateDestinationsAsync()).ReturnsAsync([destination]);
        contactService.Setup(service => service.GetContactAsync(contact.Id)).ReturnsAsync(contact);
        contactService.Setup(service => service.GetContactListsAsync()).ReturnsAsync([]);
        contactService.Setup(service => service.GetListIdsForContactAsync(contact.Id)).ReturnsAsync([]);
        var delegator = new Mock<IWinoRequestDelegator>();
        delegator.Setup(service => service.ExecuteLocalAsync(It.IsAny<IRequestBase>())).Returns(Task.CompletedTask);
        var viewModel = new ContactEditPageViewModel(
            contactService.Object,
            delegator.Object,
            Mock.Of<INavigationService>(),
            Mock.Of<IMailDialogService>(),
            Mock.Of<IContactPictureFileService>());

        viewModel.OnNavigatedTo(NavigationMode.New, new ContactEditNavigationParameter(contact.Id));
        await WaitForAsync(() => viewModel.IsEditMode);

        viewModel.SaveCommand.CanExecute(null).Should().BeFalse();
        await viewModel.ToggleFavoriteCommand.ExecuteAsync(null);

        viewModel.IsFavorite.Should().BeTrue();
        viewModel.IsDirty.Should().BeFalse();
        delegator.Verify(service => service.ExecuteLocalAsync(It.Is<ApplicationLocalContactRequest>(request =>
            request.Operation == ApplicationLocalContactOperation.SetFavorite &&
            request.Contact.Id == contact.Id &&
            request.Contact.IsFavorite &&
            !request.OriginalContact.IsFavorite)), Times.Once);
        delegator.Verify(service => service.ExecuteAsync(It.IsAny<IReadOnlyList<ContactOperationPreparationRequest>>()), Times.Never);
    }

    [Fact]
    public async Task ToggleFavorite_EditModeFailure_RevertsStateAndReportsTheError()
    {
        var contact = new AccountContact
        {
            Id = Guid.NewGuid(),
            MailAccountId = Guid.NewGuid(),
            AddressBookId = Guid.NewGuid(),
            SourceKind = ContactSourceKind.Local,
            DisplayName = "Favorite failure"
        };
        var contactService = new Mock<IContactService>();
        contactService.Setup(service => service.GetCreateDestinationsAsync()).ReturnsAsync([]);
        contactService.Setup(service => service.GetContactAsync(contact.Id)).ReturnsAsync(contact);
        contactService.Setup(service => service.GetContactListsAsync()).ReturnsAsync([]);
        contactService.Setup(service => service.GetListIdsForContactAsync(contact.Id)).ReturnsAsync([]);
        var delegator = new Mock<IWinoRequestDelegator>();
        delegator.Setup(service => service.ExecuteLocalAsync(It.IsAny<IRequestBase>()))
            .ThrowsAsync(new InvalidOperationException("Favorite persistence failed."));
        var viewModel = new ContactEditPageViewModel(
            contactService.Object,
            delegator.Object,
            Mock.Of<INavigationService>(),
            Mock.Of<IMailDialogService>(),
            Mock.Of<IContactPictureFileService>());

        viewModel.OnNavigatedTo(NavigationMode.New, new ContactEditNavigationParameter(contact.Id));
        await WaitForAsync(() => viewModel.IsEditMode);

        await viewModel.ToggleFavoriteCommand.ExecuteAsync(null);

        viewModel.IsFavorite.Should().BeFalse();
        viewModel.IsDirty.Should().BeFalse();
        viewModel.IsErrorOpen.Should().BeTrue();
        viewModel.ErrorMessage.Should().Be("Favorite persistence failed.");
    }

    [Fact]
    public async Task Save_NewFavorite_PersistsTheFavoriteInTheSingleCreateRequest()
    {
        var delegator = new Mock<IWinoRequestDelegator>();
        delegator.Setup(service => service.ExecuteAsync(It.IsAny<IReadOnlyList<ContactOperationPreparationRequest>>()))
            .Returns(Task.CompletedTask);
        delegator.Setup(service => service.ExecuteLocalAsync(It.IsAny<IRequestBase>())).Returns(Task.CompletedTask);
        var navigation = new Mock<INavigationService>();
        var viewModel = new ContactEditPageViewModel(
            Mock.Of<IContactService>(),
            delegator.Object,
            navigation.Object,
            Mock.Of<IMailDialogService>(),
            Mock.Of<IContactPictureFileService>());
        var destination = new ContactCreateDestination(
            Guid.NewGuid(), Guid.NewGuid(), ContactSourceKind.Local, "Test", "Local contacts", true);
        viewModel.Destinations.Add(destination);
        viewModel.SelectedDestination = destination;
        viewModel.DisplayName = "New favorite";

        await viewModel.ToggleFavoriteCommand.ExecuteAsync(null);
        await viewModel.SaveCommand.ExecuteAsync(null);

        delegator.Verify(service => service.ExecuteAsync(It.Is<IReadOnlyList<ContactOperationPreparationRequest>>(requests =>
            requests.Count == 1 &&
            requests[0].Operation == ContactSynchronizerOperation.Create &&
            requests[0].Contact.IsFavorite)), Times.Once);
        navigation.Verify(service => service.GoBack(), Times.Once);
    }

    [Fact]
    public async Task Save_ConcurrentInvocation_QueuesAndNavigatesExactlyOnce()
    {
        var queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delegator = new Mock<IWinoRequestDelegator>();
        delegator.Setup(service => service.ExecuteAsync(It.IsAny<IReadOnlyList<ContactOperationPreparationRequest>>()))
            .Returns(queued.Task);
        delegator.Setup(service => service.ExecuteLocalAsync(It.IsAny<IRequestBase>())).Returns(Task.CompletedTask);
        var navigation = new Mock<INavigationService>();
        var viewModel = new ContactEditPageViewModel(
            Mock.Of<IContactService>(),
            delegator.Object,
            navigation.Object,
            Mock.Of<IMailDialogService>(),
            Mock.Of<IContactPictureFileService>());
        var destination = new ContactCreateDestination(
            Guid.NewGuid(), Guid.NewGuid(), ContactSourceKind.Local, "Test", "Local contacts", true);
        viewModel.SelectedDestination = destination;
        viewModel.DisplayName = "Single save";

        var firstSave = viewModel.SaveCommand.ExecuteAsync(null);
        var secondSave = viewModel.SaveCommand.ExecuteAsync(null);
        await WaitForAsync(() => viewModel.IsSaving);
        queued.SetResult();
        await Task.WhenAll(firstSave, secondSave);

        delegator.Verify(service => service.ExecuteAsync(It.IsAny<IReadOnlyList<ContactOperationPreparationRequest>>()), Times.Once);
        delegator.Verify(service => service.ExecuteLocalAsync(It.IsAny<IRequestBase>()), Times.Once);
        navigation.Verify(service => service.SetNavigationResult(It.IsAny<NavigationResult>()), Times.Once);
        navigation.Verify(service => service.GoBack(), Times.Once);
    }

    [Fact]
    public async Task ImportedDraft_PopulatesUnsavedEditorAndKeepsNormalDestinationSelection()
    {
        var destination = new ContactCreateDestination(
            Guid.NewGuid(),
            Guid.NewGuid(),
            ContactSourceKind.CardDav,
            "Work",
            "People",
            true);
        var contactService = new Mock<IContactService>();
        contactService.Setup(service => service.GetCreateDestinationsAsync()).ReturnsAsync([destination]);
        contactService.Setup(service => service.GetContactListsAsync()).ReturnsAsync([]);
        var delegator = new Mock<IWinoRequestDelegator>();
        var viewModel = new ContactEditPageViewModel(
            contactService.Object,
            delegator.Object,
            Mock.Of<INavigationService>(),
            Mock.Of<IMailDialogService>(),
            Mock.Of<IContactPictureFileService>());
        byte[] photoBytes = [1, 2, 3, 4];
        var importedContact = new AccountContact
        {
            DisplayName = "Imported Person",
            GivenName = "Imported",
            Surname = "Person",
            CompanyName = "Wino",
            Department = "People",
            Notes = "Review before saving",
            EmailAddresses =
            [
                new ContactEmailAddress { Address = "person@example.com", IsPrimary = true }
            ],
            PhoneNumbers =
            [
                new ContactPhoneNumber { Number = "+48 123 456 789", Kind = ContactPhoneKind.Mobile }
            ]
        };

        viewModel.OnNavigatedTo(
            NavigationMode.New,
            new ContactEditNavigationParameter(ImportDraft: new(importedContact, photoBytes)));

        await WaitForAsync(() => viewModel.DisplayName == "Imported Person");

        viewModel.SelectedDestination.Should().Be(destination);
        viewModel.GivenName.Should().Be("Imported");
        viewModel.Surname.Should().Be("Person");
        viewModel.CompanyName.Should().Be("Wino");
        viewModel.Department.Should().Be("People");
        viewModel.Notes.Should().Be("Review before saving");
        viewModel.EmailAddresses.Should().ContainSingle().Which.Address.Should().Be("person@example.com");
        viewModel.PhoneNumbers.Should().ContainSingle().Which.Number.Should().Be("+48 123 456 789");
        viewModel.PreviewPhotoBytes.Should().Equal(photoBytes);
        viewModel.IsDirty.Should().BeTrue();
        delegator.Verify(service => service.ExecuteAsync(It.IsAny<ContactOperationPreparationRequest>()), Times.Never);
        delegator.Verify(service => service.ExecuteAsync(It.IsAny<IReadOnlyList<ContactOperationPreparationRequest>>()), Times.Never);
    }

    [Theory]
    [InlineData(2001, 2, 31, false)]
    [InlineData(2001, 4, 31, false)]
    [InlineData(2000, 2, 29, true)]
    [InlineData(2001, 2, 28, true)]
    public async Task Save_RejectsBirthdaysThatAreNotRealCalendarDates(int year, int month, int day, bool isValid)
    {
        var viewModel = new ContactEditPageViewModel(Mock.Of<IContactService>(), Mock.Of<IWinoRequestDelegator>(), Mock.Of<INavigationService>(), Mock.Of<IMailDialogService>(), Mock.Of<IContactPictureFileService>());
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
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanNavigateBack_DirtyEditor_FollowsTheDiscardConfirmation(bool confirmed)
    {
        var dialogs = new Mock<IMailDialogService>();
        dialogs.Setup(service => service.ShowConfirmationDialogAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>()))
            .ReturnsAsync(confirmed);
        var viewModel = CreateViewModel(dialogs: dialogs.Object);
        viewModel.MarkDirty();

        var canNavigateBack = await viewModel.CanNavigateBackAsync();

        canNavigateBack.Should().Be(confirmed);
    }

    [Fact]
    public async Task CanNavigateBack_CleanEditor_LeavesWithoutPrompting()
    {
        var dialogs = new Mock<IMailDialogService>();
        var viewModel = CreateViewModel(dialogs: dialogs.Object);

        var canNavigateBack = await viewModel.CanNavigateBackAsync();

        canNavigateBack.Should().BeTrue();
        dialogs.Verify(service => service.ShowConfirmationDialogAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task BackCommand_DelegatesTheDecisionToTheNavigationService()
    {
        var navigation = new Mock<INavigationService>();
        var viewModel = CreateViewModel(navigation: navigation.Object);
        viewModel.MarkDirty();

        await viewModel.BackCommand.ExecuteAsync(null);

        // The editor no longer decides for itself: the navigation service asks it through
        // IConfirmBackNavigation, whichever route out of the page the user took.
        navigation.Verify(service => service.GoBackAsync(It.IsAny<Wino.Core.Domain.Enums.NavigationTransitionEffect>()), Times.Once);
    }

    private static ContactEditPageViewModel CreateViewModel(
        INavigationService navigation = null,
        IMailDialogService dialogs = null)
        => new(
            Mock.Of<IContactService>(),
            Mock.Of<IWinoRequestDelegator>(),
            navigation ?? Mock.Of<INavigationService>(),
            dialogs ?? Mock.Of<IMailDialogService>(),
            Mock.Of<IContactPictureFileService>());

    private static async Task WaitForAsync(Func<bool> predicate)
    {
        var timeout = DateTime.UtcNow.AddSeconds(2);
        while (!predicate() && DateTime.UtcNow < timeout)
            await Task.Delay(10);

        predicate().Should().BeTrue();
    }

    [Fact]
    public async Task ChooseAndRemovePhoto_UpdatesTheEditorPreviewImmediately()
    {
        byte[] imageBytes = [0x89, 0x50, 0x4E, 0x47];
        var dialogs = new Mock<IMailDialogService>();
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
        var dialogs = new Mock<IMailDialogService>();
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
