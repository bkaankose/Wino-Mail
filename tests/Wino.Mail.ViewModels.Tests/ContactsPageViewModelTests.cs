using FluentAssertions;
using CommunityToolkit.Mvvm.Messaging;
using Moq;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Contacts;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Messaging.UI;
using Xunit;

namespace Wino.Mail.ViewModels.Tests;

public class ContactsPageViewModelTests
{
    [Fact]
    public async Task OverlappingReloads_AlwaysClearTheLoadingState()
    {
        var contactService = PageService();
        var viewModel = CreateViewModel(contactService.Object);

        await Task.WhenAll(viewModel.ReloadContactsCommand.ExecuteAsync(null), viewModel.ReloadContactsCommand.ExecuteAsync(null));

        viewModel.IsLoading.Should().BeFalse();
        viewModel.IsLoadingMore.Should().BeFalse();
    }

    [Fact]
    public async Task ReloadFailure_ClearsTheLoadingState()
    {
        var contactService = new Mock<IContactService>();
        contactService.Setup(service => service.GetContactsPageAsync(It.IsAny<ContactQueryFilter>(), It.IsAny<int>(), It.IsAny<int>())).ThrowsAsync(new InvalidOperationException("boom"));
        var viewModel = CreateViewModel(contactService.Object);

        await viewModel.ReloadContactsCommand.ExecuteAsync(null);

        viewModel.IsLoading.Should().BeFalse();
    }

    [Fact]
    public async Task DeletingSeveralContacts_QueuesOneBatchedRequest()
    {
        var dialogs = new Mock<IMailDialogService>();
        dialogs.Setup(service => service.ShowConfirmationDialogAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);
        var delegator = new Mock<IWinoRequestDelegator>();
        var viewModel = new ContactsPageViewModel(PageService().Object, Mock.Of<IAccountService>(), Mock.Of<ISynchronizationManager>(), delegator.Object, Mock.Of<INavigationService>(), dialogs.Object, Mock.Of<ILaunchProtocolService>());
        var accountId = Guid.NewGuid();
        var addressBookId = Guid.NewGuid();
        foreach (var name in new[] { "One", "Two", "Three" })
            viewModel.SelectedContacts.Add(new AccountContactViewModel(new AccountContact { Id = Guid.NewGuid(), MailAccountId = accountId, AddressBookId = addressBookId, SourceKind = ContactSourceKind.Local, DisplayName = name }));

        await viewModel.DeleteSelectedContactsCommand.ExecuteAsync(null);

        delegator.Verify(service => service.ExecuteAsync(It.Is<IReadOnlyList<ContactOperationPreparationRequest>>(requests => requests.Count == 3)), Times.Once);
        delegator.Verify(service => service.ExecuteAsync(It.IsAny<ContactOperationPreparationRequest>()), Times.Never);
    }

    [Fact]
    public async Task SearchContacts_ReturnsSuggestionsWithoutReplacingTheLoadedList()
    {
        var loadedContact = Contact("Loaded");
        var suggestedContact = Contact("Suggested");
        var contactService = new Mock<IContactService>();
        contactService.Setup(service => service.GetContactsPageAsync(
                It.Is<ContactQueryFilter>(filter => filter.SearchQuery == "suggest"), 0, 6))
            .ReturnsAsync(new PagedContactsResult([suggestedContact], 1, false, 0, 6));
        var viewModel = CreateViewModel(contactService.Object);
        viewModel.Contacts.Add(new AccountContactViewModel(loadedContact));

        var suggestions = await viewModel.SearchContactsAsync("suggest", 6);

        suggestions.Should().ContainSingle(item => item.Id == suggestedContact.Id);
        viewModel.Contacts.Should().ContainSingle(item => item.Id == loadedContact.Id);
    }

    [Fact]
    public async Task LoadAndSelectContact_LoadsAdditionalPagesUntilTheContactIsAvailable()
    {
        var firstContact = Contact("Alpha");
        var targetContact = Contact("Zulu");
        var contactService = new Mock<IContactService>();
        contactService.Setup(service => service.GetContactsPageAsync(It.IsAny<ContactQueryFilter>(), 0, 50))
            .ReturnsAsync(new PagedContactsResult([firstContact], 2, true, 0, 50));
        contactService.Setup(service => service.GetContactsPageAsync(It.IsAny<ContactQueryFilter>(), 1, 50))
            .ReturnsAsync(new PagedContactsResult([targetContact], 2, false, 1, 50));
        var viewModel = CreateViewModel(contactService.Object);
        await viewModel.ReloadContactsCommand.ExecuteAsync(null);

        var selectedContact = await viewModel.LoadAndSelectContactAsync(targetContact.Id);

        selectedContact.Should().NotBeNull();
        selectedContact!.Id.Should().Be(targetContact.Id);
        viewModel.SelectedContact.Should().BeSameAs(selectedContact);
        viewModel.Contacts.Should().HaveCount(2);
    }

    [Fact]
    public async Task BackNavigation_PreservesTheLoadedContactsWithoutReloading()
    {
        var accountService = new Mock<IAccountService>();
        accountService.Setup(service => service.GetAccountsAsync()).ReturnsAsync([]);
        var contactService = PageService();
        contactService.Setup(service => service.GetAddressBooksAsync(null)).ReturnsAsync([]);
        contactService.Setup(service => service.GetContactListsAsync()).ReturnsAsync([]);
        contactService.Setup(service => service.GetContactListCountsAsync()).ReturnsAsync([]);
        contactService.Setup(service => service.GetFavoriteContactsCountAsync()).ReturnsAsync(0);
        var viewModel = new ContactsPageViewModel(contactService.Object, accountService.Object,
            Mock.Of<ISynchronizationManager>(), Mock.Of<IWinoRequestDelegator>(),
            Mock.Of<INavigationService>(), Mock.Of<IMailDialogService>(), Mock.Of<ILaunchProtocolService>());

        viewModel.OnNavigatedTo(NavigationMode.New, null!);
        await WaitUntilAsync(() => contactService.Invocations.Count(invocation =>
            invocation.Method.Name == nameof(IContactService.GetContactsPageAsync)) == 1 && !viewModel.IsLoading);
        await Task.Delay(25);
        var initialPageLoadCount = contactService.Invocations.Count(invocation =>
            invocation.Method.Name == nameof(IContactService.GetContactsPageAsync));

        viewModel.OnNavigatedTo(NavigationMode.Back, null!);
        await Task.Delay(25);

        accountService.Verify(service => service.GetAccountsAsync(), Times.Once);
        contactService.Verify(service => service.GetContactsPageAsync(
            It.IsAny<ContactQueryFilter>(), It.IsAny<int>(), It.IsAny<int>()), Times.Exactly(initialPageLoadCount));
    }

    [Fact]
    public async Task ExplicitRefresh_IgnoresItsOwnCompletionMessageAndReloadsOnce()
    {
        var account = new MailAccount { Id = Guid.NewGuid(), Name = "Outlook", IsContactAccessGranted = true };
        var accountService = new Mock<IAccountService>();
        accountService.Setup(service => service.GetAccountsAsync()).ReturnsAsync([account]);
        var contactService = PageService();
        contactService.Setup(service => service.GetAddressBooksAsync(null)).ReturnsAsync([]);
        contactService.Setup(service => service.GetContactListsAsync()).ReturnsAsync([]);
        contactService.Setup(service => service.GetContactListCountsAsync()).ReturnsAsync([]);
        contactService.Setup(service => service.GetFavoriteContactsCountAsync()).ReturnsAsync(0);
        var synchronizationManager = new Mock<ISynchronizationManager>();
        ContactsPageViewModel viewModel = null!;
        synchronizationManager.Setup(service => service.SynchronizeContactsAsync(
                It.Is<ContactSynchronizationOptions>(options => options.AccountId == account.Id),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                ((IRecipient<ContactSynchronizationCompleted>)viewModel).Receive(
                    new ContactSynchronizationCompleted(account.Id, SynchronizationCompletedState.Success));
                return ContactSynchronizationResult.Empty;
            });
        viewModel = new ContactsPageViewModel(contactService.Object, accountService.Object,
            synchronizationManager.Object, Mock.Of<IWinoRequestDelegator>(), Mock.Of<INavigationService>(),
            Mock.Of<IMailDialogService>(), Mock.Of<ILaunchProtocolService>());

        viewModel.OnNavigatedTo(NavigationMode.New, null!);
        await WaitUntilAsync(() => contactService.Invocations.Count(invocation =>
            invocation.Method.Name == nameof(IContactService.GetContactsPageAsync)) == 1 && !viewModel.IsLoading);
        contactService.Invocations.Clear();

        await viewModel.RefreshContactsCommand.ExecuteAsync(null);
        await Task.Delay(500);

        contactService.Verify(service => service.GetContactsPageAsync(
            It.IsAny<ContactQueryFilter>(), It.IsAny<int>(), It.IsAny<int>()), Times.Once);
    }

    [Fact]
    public void ComposeToContact_IsAvailableOnlyWhenTheContactHasAnEmailAddress()
    {
        var viewModel = CreateViewModel(PageService().Object);
        var withoutEmail = new AccountContactViewModel(Contact("Phone only"));
        var contactWithEmail = Contact("Email contact");
        contactWithEmail.Address = "person@example.com";
        var withEmail = new AccountContactViewModel(contactWithEmail);

        viewModel.ComposeToContactCommand.CanExecute(withoutEmail).Should().BeFalse();
        viewModel.ComposeToContactCommand.CanExecute(withEmail).Should().BeTrue();
    }

    private static AccountContact Contact(string name)
        => new()
        {
            Id = Guid.NewGuid(),
            DisplayName = name,
            SortKey = name,
            SourceKind = ContactSourceKind.Local
        };

    private static Mock<IContactService> PageService()
    {
        var mock = new Mock<IContactService>();
        mock.Setup(service => service.GetContactsPageAsync(It.IsAny<ContactQueryFilter>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync(new PagedContactsResult([], 0, false, 0, 50));
        return mock;
    }

    private static ContactsPageViewModel CreateViewModel(IContactService contactService)
        => new(contactService, Mock.Of<IAccountService>(), Mock.Of<ISynchronizationManager>(), Mock.Of<IWinoRequestDelegator>(), Mock.Of<INavigationService>(), Mock.Of<IMailDialogService>(), Mock.Of<ILaunchProtocolService>());

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow.AddSeconds(2);
        while (!condition() && DateTime.UtcNow < timeout)
            await Task.Delay(10);

        condition().Should().BeTrue();
    }
}
