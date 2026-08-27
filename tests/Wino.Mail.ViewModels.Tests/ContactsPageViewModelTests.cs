using FluentAssertions;
using CommunityToolkit.Mvvm.Messaging;
using Moq;
using System.Collections.Specialized;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.MenuItems;
using Wino.Core.Domain.Models.Contacts;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Requests.Contact;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Messaging.UI;
using Xunit;

namespace Wino.Mail.ViewModels.Tests;

public class ContactsPageViewModelTests
{
    [Fact]
    public async Task ImportedDraft_OpensEditorAfterPeopleRootInitializes()
    {
        var contactService = PageService();
        contactService.Setup(service => service.GetCreateDestinationsAsync()).ReturnsAsync(
        [
            new ContactCreateDestination(Guid.NewGuid(), Guid.NewGuid(), ContactSourceKind.Local, "Account", "People", true)
        ]);
        var accountService = new Mock<IAccountService>();
        accountService.Setup(service => service.GetAccountsAsync()).ReturnsAsync([]);
        var navigation = new Mock<INavigationService>();
        var navigated = false;
        navigation.Setup(service => service.Navigate(
                WinoPage.ContactEditPage,
                It.IsAny<object>(),
                It.IsAny<NavigationReferenceFrame?>(),
                It.IsAny<NavigationTransitionType>()))
            .Callback(() => navigated = true)
            .Returns(true);
        var dialogs = new Mock<IMailDialogService>();
        var viewModel = new ContactsPageViewModel(
            contactService.Object,
            accountService.Object,
            Mock.Of<ISynchronizationManager>(),
            Mock.Of<IWinoRequestDelegator>(),
            navigation.Object,
            dialogs.Object,
            Mock.Of<ILaunchProtocolService>())
        {
            Dispatcher = new ImmediateDispatcher()
        };
        var parameter = new ContactEditNavigationParameter(
            ImportDraft: new(new AccountContact { DisplayName = "Imported" }, HasUnsupportedContent: true));

        viewModel.OnNavigatedTo(NavigationMode.New, parameter);
        await WaitUntilAsync(() => navigated);

        navigation.Verify(service => service.Navigate(
            WinoPage.ContactEditPage,
            It.Is<ContactEditNavigationParameter>(value => ReferenceEquals(value, parameter)),
            It.IsAny<NavigationReferenceFrame?>(),
            It.IsAny<NavigationTransitionType>()), Times.Once);
        dialogs.Verify(service => service.InfoBarMessage(
            It.IsAny<string>(),
            It.IsAny<string>(),
            InfoBarMessageType.Warning), Times.Once);
    }

    [Fact]
    public async Task ImportedDraftWithoutDestination_RemainsAtPeopleRootAndWarns()
    {
        var contactService = PageService();
        contactService.Setup(service => service.GetCreateDestinationsAsync()).ReturnsAsync([]);
        var accountService = new Mock<IAccountService>();
        accountService.Setup(service => service.GetAccountsAsync()).ReturnsAsync([]);
        var navigation = new Mock<INavigationService>();
        var dialogs = new Mock<IMailDialogService>();
        var warningShown = false;
        dialogs.Setup(service => service.InfoBarMessage(It.IsAny<string>(), It.IsAny<string>(), InfoBarMessageType.Warning))
            .Callback(() => warningShown = true);
        var viewModel = new ContactsPageViewModel(
            contactService.Object,
            accountService.Object,
            Mock.Of<ISynchronizationManager>(),
            Mock.Of<IWinoRequestDelegator>(),
            navigation.Object,
            dialogs.Object,
            Mock.Of<ILaunchProtocolService>())
        {
            Dispatcher = new ImmediateDispatcher()
        };

        viewModel.OnNavigatedTo(
            NavigationMode.New,
            new ContactEditNavigationParameter(ImportDraft: new(new AccountContact { DisplayName = "Imported" })));
        await WaitUntilAsync(() => warningShown);

        navigation.Verify(service => service.Navigate(
            WinoPage.ContactEditPage,
            It.IsAny<object>(),
            It.IsAny<NavigationReferenceFrame?>(),
            It.IsAny<NavigationTransitionType>()), Times.Never);
    }

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
    public async Task BackNavigation_ReconcilesTheLoadedContactsWithoutResettingTheGroups()
    {
        var original = Contact("Alpha");
        var updated = Contact("Alpha updated");
        updated.Id = original.Id;
        updated.SortKey = original.SortKey;
        updated.ModifiedAtUtc = original.ModifiedAtUtc.AddMinutes(1);
        var added = Contact("Able");
        var accountService = new Mock<IAccountService>();
        accountService.Setup(service => service.GetAccountsAsync()).ReturnsAsync([]);
        var contactService = PageService();
        contactService.SetupSequence(service => service.GetContactsPageAsync(
                It.IsAny<ContactQueryFilter>(), 0, 50))
            .ReturnsAsync(new PagedContactsResult([original], 1, false, 0, 50))
            .ReturnsAsync(new PagedContactsResult([added, updated], 2, false, 0, 50));
        var viewModel = new ContactsPageViewModel(contactService.Object, accountService.Object,
            Mock.Of<ISynchronizationManager>(), Mock.Of<IWinoRequestDelegator>(),
            Mock.Of<INavigationService>(), Mock.Of<IMailDialogService>(), Mock.Of<ILaunchProtocolService>());

        viewModel.OnNavigatedTo(NavigationMode.New, null!);
        await WaitUntilAsync(() => contactService.Invocations.Count(invocation =>
            invocation.Method.Name == nameof(IContactService.GetContactsPageAsync)) == 1 && !viewModel.IsLoading);
        var groupActions = new List<NotifyCollectionChangedAction>();
        ((INotifyCollectionChanged)viewModel.ContactGroups[0]).CollectionChanged += (_, args) => groupActions.Add(args.Action);

        viewModel.OnNavigatedTo(NavigationMode.Back, null!);
        await WaitUntilAsync(() => viewModel.Contacts.Count == 2 && viewModel.Contacts.Any(item => item.Name == "Alpha updated"));

        accountService.Verify(service => service.GetAccountsAsync(), Times.Once);
        contactService.Verify(service => service.GetContactsPageAsync(
            It.IsAny<ContactQueryFilter>(), 0, 50), Times.Exactly(2));
        groupActions.Should().Contain(NotifyCollectionChangedAction.Add);
        groupActions.Should().Contain(NotifyCollectionChangedAction.Replace);
        groupActions.Should().NotContain(NotifyCollectionChangedAction.Reset);
    }

    [Fact]
    public async Task Synchronization_ReconcilesAddRemoveAndReplaceWithoutReset()
    {
        var alpha = Contact("Alpha");
        var beta = Contact("Beta");
        var updatedAlpha = Contact("Alpha updated");
        updatedAlpha.Id = alpha.Id;
        updatedAlpha.SortKey = alpha.SortKey;
        updatedAlpha.ModifiedAtUtc = alpha.ModifiedAtUtc.AddMinutes(1);
        var gamma = Contact("Gamma");
        var accountService = new Mock<IAccountService>();
        accountService.Setup(service => service.GetAccountsAsync()).ReturnsAsync([]);
        var contactService = PageService();
        contactService.SetupSequence(service => service.GetContactsPageAsync(
                It.IsAny<ContactQueryFilter>(), 0, 50))
            .ReturnsAsync(new PagedContactsResult([alpha, beta], 2, false, 0, 50))
            .ReturnsAsync(new PagedContactsResult([updatedAlpha, gamma], 2, false, 0, 50));
        var viewModel = new ContactsPageViewModel(contactService.Object, accountService.Object,
            Mock.Of<ISynchronizationManager>(), Mock.Of<IWinoRequestDelegator>(),
            Mock.Of<INavigationService>(), Mock.Of<IMailDialogService>(), Mock.Of<ILaunchProtocolService>());
        viewModel.OnNavigatedTo(NavigationMode.New, null!);
        await WaitUntilAsync(() => viewModel.Contacts.Count == 2 && !viewModel.IsLoading);
        var actions = new List<NotifyCollectionChangedAction>();
        viewModel.Contacts.CollectionChanged += (_, args) => actions.Add(args.Action);

        ((IRecipient<ContactSynchronizationCompleted>)viewModel).Receive(
            new ContactSynchronizationCompleted(Guid.NewGuid(), SynchronizationCompletedState.Success));

        await WaitUntilAsync(() => viewModel.Contacts.Any(item => item.Id == gamma.Id));
        actions.Should().Contain(NotifyCollectionChangedAction.Add);
        actions.Should().Contain(NotifyCollectionChangedAction.Remove);
        actions.Should().Contain(NotifyCollectionChangedAction.Replace);
        actions.Should().NotContain(NotifyCollectionChangedAction.Reset);
    }

    [Fact]
    public async Task DeleteContact_RemovesTheLoadedItemWithoutReloading()
    {
        var contact = Contact("Delete me");
        var accountService = new Mock<IAccountService>();
        accountService.Setup(service => service.GetAccountsAsync()).ReturnsAsync([]);
        var contactService = PageService();
        contactService.Setup(service => service.GetContactsPageAsync(
                It.IsAny<ContactQueryFilter>(), 0, 50))
            .ReturnsAsync(new PagedContactsResult([contact], 1, false, 0, 50));
        var dialogs = new Mock<IMailDialogService>();
        dialogs.Setup(service => service.ShowConfirmationDialogAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);
        var viewModel = new ContactsPageViewModel(contactService.Object, accountService.Object,
            Mock.Of<ISynchronizationManager>(), Mock.Of<IWinoRequestDelegator>(),
            Mock.Of<INavigationService>(), dialogs.Object, Mock.Of<ILaunchProtocolService>());
        viewModel.OnNavigatedTo(NavigationMode.New, null!);
        await WaitUntilAsync(() => viewModel.Contacts.Count == 1 && !viewModel.IsLoading);

        await viewModel.DeleteContactCommand.ExecuteAsync(viewModel.Contacts[0]);

        viewModel.Contacts.Should().BeEmpty();
        viewModel.ContactGroups.Count.Should().Be(0);
        contactService.Verify(service => service.GetContactsPageAsync(
            It.IsAny<ContactQueryFilter>(), It.IsAny<int>(), It.IsAny<int>()), Times.Once);
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

    [Fact]
    public void ContactActionAvailability_ReflectsAuthorizationAndEmailAddress()
    {
        var unauthorizedContact = Contact("Read only");
        unauthorizedContact.SourceKind = ContactSourceKind.Outlook;
        var withoutEmail = new AccountContactViewModel(unauthorizedContact, isAuthorized: false);
        var editableContact = Contact("Editable");
        editableContact.Address = "person@example.com";
        var withEmail = new AccountContactViewModel(editableContact);

        withoutEmail.CanEdit.Should().BeFalse();
        withoutEmail.CanDelete.Should().BeFalse();
        withoutEmail.CanSendMail.Should().BeFalse();
        withEmail.CanEdit.Should().BeTrue();
        withEmail.CanDelete.Should().BeTrue();
        withEmail.CanSendMail.Should().BeTrue();
    }

    [Fact]
    public void FavoriteActionText_TracksTheCurrentFavoriteState()
    {
        var contact = new AccountContactViewModel(Contact("Favorite"));

        contact.FavoriteActionText.Should().Be(Translator.ContactAction_Favorite);

        contact.IsFavorite = true;

        contact.FavoriteActionText.Should().Be(Translator.ContactAction_Unfavorite);
    }

    [Fact]
    public async Task GetAssignableLists_ExcludesExistingMemberships()
    {
        var contact = new AccountContactViewModel(Contact("Listed contact"));
        var assignedList = new ContactList { Id = Guid.NewGuid(), Name = "Assigned" };
        var availableList = new ContactList { Id = Guid.NewGuid(), Name = "Available" };
        var contactService = PageService();
        contactService.Setup(service => service.GetListIdsForContactAsync(contact.Id))
            .ReturnsAsync([assignedList.Id]);
        var viewModel = CreateViewModel(contactService.Object);
        viewModel.ContactLists.Add(assignedList);
        viewModel.ContactLists.Add(availableList);

        var result = await viewModel.GetAssignableListsAsync(contact);

        result.Should().ContainSingle().Which.Should().BeSameAs(availableList);
    }

    [Fact]
    public async Task AssignContactsToList_DeduplicatesIdsAndRefreshesListCounts()
    {
        var list = new ContactList { Id = Guid.NewGuid(), Name = "Friends" };
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var contactService = PageService();
        var dialogs = new Mock<IMailDialogService>();
        ApplicationLocalContactRequest queuedRequest = null;
        var delegator = new Mock<IWinoRequestDelegator>();
        delegator.Setup(service => service.ExecuteLocalAsync(It.IsAny<IRequestBase>()))
            .Callback<IRequestBase>(request => queuedRequest = request as ApplicationLocalContactRequest)
            .Returns(Task.CompletedTask);
        var viewModel = new ContactsPageViewModel(contactService.Object, Mock.Of<IAccountService>(),
            Mock.Of<ISynchronizationManager>(), delegator.Object, Mock.Of<INavigationService>(),
            dialogs.Object, Mock.Of<ILaunchProtocolService>());

        await viewModel.AssignContactsToListAsync(list, [firstId, firstId, secondId, Guid.Empty]);

        queuedRequest.Should().NotBeNull();
        queuedRequest.Operation.Should().Be(ApplicationLocalContactOperation.AddMembership);
        queuedRequest.ContactIds.Should().Equal(firstId, secondId);
        contactService.Verify(service => service.AddContactsToListAsync(
            It.IsAny<Guid>(),
            It.IsAny<IEnumerable<Guid>>()), Times.Never);
        dialogs.Verify(service => service.InfoBarMessage(
            Translator.ContactList_AddedTitle,
            It.IsAny<string>(),
            InfoBarMessageType.Success), Times.Once);
    }

    [Fact]
    public async Task AssignContactsToList_UpdatesCountWithoutRefreshingNavigationCollections()
    {
        var list = new ContactList { Id = Guid.NewGuid(), Name = "Friends" };
        var contactService = PageService();
        contactService.Setup(service => service.GetContactListsAsync()).ReturnsAsync([list]);
        contactService.SetupSequence(service => service.GetContactListCountsAsync())
            .ReturnsAsync(new Dictionary<Guid, int> { [list.Id] = 1 })
            .ReturnsAsync(new Dictionary<Guid, int> { [list.Id] = 2 });
        var accountService = new Mock<IAccountService>();
        accountService.Setup(service => service.GetAccountsAsync()).ReturnsAsync([]);
        var delegator = new Mock<IWinoRequestDelegator>();
        delegator.Setup(service => service.ExecuteLocalAsync(It.IsAny<IRequestBase>()))
            .Callback<IRequestBase>(request => request.ApplyUIChanges())
            .Returns(Task.CompletedTask);
        var viewModel = new ContactsPageViewModel(contactService.Object, accountService.Object,
            Mock.Of<ISynchronizationManager>(), delegator.Object, Mock.Of<INavigationService>(),
            Mock.Of<IMailDialogService>(), Mock.Of<ILaunchProtocolService>());

        viewModel.OnNavigatedTo(NavigationMode.New, null!);
        await WaitUntilAsync(() => viewModel.FilterGroups.SelectMany(group => group).Any(filter => filter.ListId == list.Id));
        var listGroup = viewModel.FilterGroups.Single(group => group.Any(filter => filter.IsList));
        var listFilter = listGroup.Single(filter => filter.ListId == list.Id);
        var groupActions = new List<NotifyCollectionChangedAction>();
        var itemActions = new List<NotifyCollectionChangedAction>();
        viewModel.FilterGroups.CollectionChanged += (_, args) => groupActions.Add(args.Action);
        listGroup.CollectionChanged += (_, args) => itemActions.Add(args.Action);

        await viewModel.AssignContactsToListAsync(list, [Guid.NewGuid()]);

        listFilter.Count.Should().Be(2);
        viewModel.FilterGroups.Single(group => group.Any(filter => filter.IsList)).Should().BeSameAs(listGroup);
        groupActions.Should().BeEmpty();
        itemActions.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateList_AddsOneNavigationItemWithoutResettingGroups()
    {
        var contactService = PageService();
        var accountService = new Mock<IAccountService>();
        accountService.Setup(service => service.GetAccountsAsync()).ReturnsAsync([]);
        var dialogs = new Mock<IMailDialogService>();
        dialogs.SetupSequence(service => service.ShowTextInputDialogAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync("Team")
            .ReturnsAsync("Renamed team");
        var localRequests = new List<ApplicationLocalContactRequest>();
        var delegator = new Mock<IWinoRequestDelegator>();
        delegator.Setup(service => service.ExecuteLocalAsync(It.IsAny<IRequestBase>()))
            .Callback<IRequestBase>(request =>
            {
                localRequests.Add((ApplicationLocalContactRequest)request);
                request.ApplyUIChanges();
            })
            .Returns(Task.CompletedTask);
        var viewModel = new ContactsPageViewModel(contactService.Object, accountService.Object,
            Mock.Of<ISynchronizationManager>(), delegator.Object, Mock.Of<INavigationService>(),
            dialogs.Object, Mock.Of<ILaunchProtocolService>());

        viewModel.OnNavigatedTo(NavigationMode.New, null!);
        await WaitUntilAsync(() => viewModel.FilterGroups.Count == 2 && !viewModel.IsLoading);
        var listGroup = viewModel.FilterGroups.Last();
        var groupActions = new List<NotifyCollectionChangedAction>();
        var itemActions = new List<NotifyCollectionChangedAction>();
        viewModel.FilterGroups.CollectionChanged += (_, args) => groupActions.Add(args.Action);
        listGroup.CollectionChanged += (_, args) => itemActions.Add(args.Action);

        await viewModel.CreateListCommand.ExecuteAsync(null);

        viewModel.FilterGroups.Last().Should().BeSameAs(listGroup);
        viewModel.ContactLists.Should().ContainSingle().Which.Name.Should().Be("Team");
        itemActions.Should().Equal(NotifyCollectionChangedAction.Add);
        groupActions.Should().BeEmpty();

        var createdFilter = listGroup.Should().ContainSingle().Which;
        createdFilter.RenameListCommand.Execute(null);
        await WaitUntilAsync(() => createdFilter.Name == "Renamed team");

        localRequests.Select(request => request.Operation).Should().Equal(
            ApplicationLocalContactOperation.CreateList,
            ApplicationLocalContactOperation.UpdateList);
        contactService.Verify(service => service.CreateContactListAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        contactService.Verify(service => service.UpdateContactListAsync(It.IsAny<ContactList>()), Times.Never);
    }

    [Fact]
    public void ResolveContactDragIds_SelectedSourceCarriesTheFullSelection()
    {
        var first = new AccountContactViewModel(Contact("First"));
        var second = new AccountContactViewModel(Contact("Second"));
        var viewModel = CreateViewModel(PageService().Object);
        viewModel.SelectedContacts.Add(first);
        viewModel.SelectedContacts.Add(second);

        var result = viewModel.ResolveContactDragIds([first]);

        result.Should().Equal(first.Id, second.Id);
    }

    [Fact]
    public void ResolveContactDragIds_UnselectedSourceCarriesOnlyTheDraggedContact()
    {
        var first = new AccountContactViewModel(Contact("First"));
        var second = new AccountContactViewModel(Contact("Second"));
        var unselected = new AccountContactViewModel(Contact("Unselected"));
        var viewModel = CreateViewModel(PageService().Object);
        viewModel.SelectedContacts.Add(first);
        viewModel.SelectedContacts.Add(second);

        var result = viewModel.ResolveContactDragIds([unselected]);

        result.Should().Equal(unselected.Id);
    }

    [Fact]
    public async Task LeavingTheContactsPage_DisablesEveryPaneEntry()
    {
        var viewModel = await NavigatedViewModelAsync();

        viewModel.OnNavigatedFrom(NavigationMode.New, null);

        InteractivePaneEntries(viewModel).Should().NotBeEmpty().And.OnlyContain(item => !item.IsEnabled);
    }

    [Fact]
    public async Task ReturningToTheContactsPage_EnablesEveryPaneEntry()
    {
        var viewModel = await NavigatedViewModelAsync();
        viewModel.OnNavigatedFrom(NavigationMode.New, null);

        viewModel.OnNavigatedTo(NavigationMode.Back, null);

        InteractivePaneEntries(viewModel).Should().NotBeEmpty().And.OnlyContain(item => item.IsEnabled);
    }

    [Fact]
    public async Task CollapsingThePaneWhileTheEditorIsOpen_KeepsTheEntriesDisabled()
    {
        var viewModel = await NavigatedViewModelAsync();
        viewModel.OnNavigatedFrom(NavigationMode.New, null);

        // Resizing the window rebuilds the pane contents behind the editor.
        viewModel.ShellMenuProvider.SetPaneCompact(true);

        InteractivePaneEntries(viewModel).Should().NotBeEmpty().And.OnlyContain(item => !item.IsEnabled);
    }

    [Fact]
    public async Task InvokingAPaneEntryWhileDisabled_DoesNothing()
    {
        var viewModel = await NavigatedViewModelAsync();
        var favorites = viewModel.FilterGroups.SelectMany(group => group).First(filter => filter.Kind == ContactFilterKind.Favorites);
        viewModel.OnNavigatedFrom(NavigationMode.New, null);

        await viewModel.ShellMenuProvider.OnMenuItemInvokedAsync(favorites);

        viewModel.SelectedFilter.Should().NotBe(favorites);
    }

    /// <summary>Every pane entry the user can actually invoke. Section captions are not one.</summary>
    private static IReadOnlyList<MenuItemBase> InteractivePaneEntries(ContactsPageViewModel viewModel)
        => viewModel.ShellMenu.Items.OfType<MenuItemBase>().Where(item => item is not ShellSectionHeaderMenuItem).ToList();

    private static async Task<ContactsPageViewModel> NavigatedViewModelAsync(Mock<IContactService> contactService = null)
    {
        var accountService = new Mock<IAccountService>();
        accountService.Setup(service => service.GetAccountsAsync()).ReturnsAsync([]);

        var viewModel = new ContactsPageViewModel(
            (contactService ?? PageService()).Object,
            accountService.Object,
            Mock.Of<ISynchronizationManager>(),
            Mock.Of<IWinoRequestDelegator>(),
            Mock.Of<INavigationService>(),
            Mock.Of<IMailDialogService>(),
            Mock.Of<ILaunchProtocolService>())
        {
            Dispatcher = new ImmediateDispatcher()
        };

        viewModel.OnNavigatedTo(NavigationMode.New, null);
        await WaitUntilAsync(() => viewModel.ShellMenu?.Items.Count > 2);

        return viewModel;
    }

    private sealed class ImmediateDispatcher : IDispatcher
    {
        public Task ExecuteOnUIThread(Action action)
        {
            action();
            return Task.CompletedTask;
        }
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
        mock.Setup(service => service.GetAddressBooksAsync(null)).ReturnsAsync([]);
        mock.Setup(service => service.GetContactListsAsync()).ReturnsAsync([]);
        mock.Setup(service => service.GetContactListCountsAsync()).ReturnsAsync([]);
        mock.Setup(service => service.GetFavoriteContactsCountAsync()).ReturnsAsync(0);
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
