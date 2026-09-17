using FluentAssertions;
using Moq;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Contacts;
using Wino.Core.Domain.Models.Launch;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.PublicFolders;
using Wino.Core.Requests.Contact;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Messaging.UI;
using Xunit;

namespace Wino.Mail.ViewModels.Tests;

public class ContactsPagePublicFolderTests
{
    private readonly MailAccount _exchange = new() { Id = Guid.NewGuid(), Name = "Work", Address = "user@contoso.test", ProviderType = MailProviderType.Exchange };
    private readonly MailAccount _imap = new() { Id = Guid.NewGuid(), Name = "Home", ProviderType = MailProviderType.IMAP4, IsContactAccessGranted = true };
    private readonly List<PublicFolderFavorite> _favorites = [];
    private readonly Mock<IPublicFolderFavoriteService> _favoriteService = new();
    private readonly Mock<IPublicFolderService> _publicFolderService = new();
    private readonly Mock<IContactService> _contactService = new();
    private readonly Mock<IWinoRequestDelegator> _delegator = new();
    private readonly Mock<ILaunchProtocolService> _launchProtocol = new();

    public ContactsPagePublicFolderTests()
    {
        _favoriteService.Setup(service => service.GetFavorites()).Returns(() => _favorites.ToList());
        _favoriteService
            .Setup(service => service.RemoveFavorite(It.IsAny<Guid>(), It.IsAny<string>()))
            .Callback<Guid, string>((accountId, folderId) => _favorites.RemoveAll(favorite => favorite.AccountId == accountId && favorite.FolderId == folderId));

        _publicFolderService
            .Setup(service => service.GetContactsAsync(_exchange.Id, "staff", It.IsAny<CancellationToken>()))
            .ReturnsAsync((PublicFolderContact[])
            [
                new PublicFolderContact { RemoteId = "2", DisplayName = "Zoe Zimmer", Address = "zoe@contoso.test", Company = "Contoso", BusinessPhone = "555 0102" },
                new PublicFolderContact { RemoteId = "1", DisplayName = "Adam Apple", Company = "Fabrikam", MobilePhone = "555 0101" }
            ]);

        _contactService.Setup(service => service.GetContactsPageAsync(It.IsAny<ContactQueryFilter>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<ContactSortOrder>()))
            .ReturnsAsync(new PagedContactsResult([], 0, false, 0, 50));
        _contactService.Setup(service => service.GetAddressBooksAsync(null)).ReturnsAsync([]);
        _contactService.Setup(service => service.GetContactListsAsync()).ReturnsAsync([]);
        _contactService.Setup(service => service.GetContactListCountsAsync()).ReturnsAsync([]);
        _contactService.Setup(service => service.GetFavoriteContactsCountAsync()).ReturnsAsync(0);
    }

    [Fact]
    public async Task PinnedContactFolders_ShowOnlyForTheirExchangeAccount()
    {
        _favorites.Add(PublicFolderFavorite.Create(_exchange.Id, "staff", PublicFolderKind.Contacts, "Staff"));
        _favorites.Add(PublicFolderFavorite.Create(_exchange.Id, "sales", PublicFolderKind.Mail, "Sales"));
        _favorites.Add(PublicFolderFavorite.Create(_exchange.Id, "company", PublicFolderKind.Calendar, "Company"));
        _favorites.Add(PublicFolderFavorite.Create(_imap.Id, "other", PublicFolderKind.Contacts, "Not Exchange"));
        _favorites.Add(PublicFolderFavorite.Create(Guid.NewGuid(), "gone", PublicFolderKind.Contacts, "Removed account"));

        var viewModel = await NavigatedViewModelAsync();

        var entry = PublicFolderEntries(viewModel).Should().ContainSingle().Subject;
        entry.PublicFolderId.Should().Be("staff");
        entry.Account.Should().BeSameAs(_exchange);
        entry.Name.Should().StartWith("Staff ");
        entry.CanRenameOrDelete.Should().BeFalse();
        entry.SupportsAccountSynchronization.Should().BeFalse();
        entry.UnpinCommand.CanExecute(null).Should().BeTrue();
        viewModel.ShellMenu.Items.Should().Contain(entry);
    }

    [Fact]
    public async Task SelectingAPinnedFolder_ListsItsContactsReadOnly()
    {
        _favorites.Add(PublicFolderFavorite.Create(_exchange.Id, "staff", PublicFolderKind.Contacts, "Staff"));
        var viewModel = await NavigatedViewModelAsync();

        viewModel.SelectedFilter = PublicFolderEntries(viewModel).Single();
        await WaitUntilAsync(() => viewModel.Contacts.Count == 2 && !viewModel.IsLoading);

        viewModel.Contacts.Select(contact => contact.Name).Should().Equal("Adam Apple", "Zoe Zimmer");
        viewModel.ContactGroups.Select(group => group.Key).Should().Equal("A", "Z");
        viewModel.TotalContactsCount.Should().Be(2);
        viewModel.HasMoreContacts.Should().BeFalse();
        viewModel.Contacts.Should().OnlyContain(contact =>
            contact.IsReadOnlySource && !contact.IsEditable && !contact.CanEdit && !contact.CanDelete && !contact.CanFavorite);

        var zoe = viewModel.Contacts[1];
        zoe.SecondaryValue.Should().Be("zoe@contoso.test");
        zoe.JobTitleOrCompany.Should().Be("Contoso");
        zoe.SourceContact.PrimaryPhoneNumber.Should().Be("555 0102");
        zoe.SourceLabel.Should().Contain("Staff");
        viewModel.Contacts[0].SecondaryValue.Should().Be("555 0101");

        _contactService.Verify(service => service.GetContactsPageAsync(
            It.Is<ContactQueryFilter>(filter => filter.AddressBookId == null && filter.ListId == null && !filter.FavoritesOnly),
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<ContactSortOrder>()), Times.AtMostOnce(),
            "only the initial 'all contacts' load reaches the contact store");
    }

    [Fact]
    public async Task ReadOnlyContacts_TakeNoStoreWritesButStillComposeMail()
    {
        _favorites.Add(PublicFolderFavorite.Create(_exchange.Id, "staff", PublicFolderKind.Contacts, "Staff"));
        var viewModel = await NavigatedViewModelAsync();
        viewModel.SelectedFilter = PublicFolderEntries(viewModel).Single();
        await WaitUntilAsync(() => viewModel.Contacts.Count == 2 && !viewModel.IsLoading);
        var zoe = viewModel.Contacts[1];
        var adam = viewModel.Contacts[0];

        await viewModel.ToggleFavoriteCommand.ExecuteAsync(zoe);
        viewModel.SelectedContacts.Add(zoe);
        await viewModel.FavoriteSelectedContactsCommand.ExecuteAsync(null);
        await viewModel.DeleteContactCommand.ExecuteAsync(zoe);
        viewModel.EditContactCommand.Execute(zoe);

        zoe.IsFavorite.Should().BeFalse();
        (await viewModel.GetAssignableListsAsync(zoe)).Should().BeEmpty();
        viewModel.ResolveContactDragIds((AccountContactViewModel[])[zoe]).Should().BeEmpty();
        _delegator.VerifyNoOtherCalls();

        viewModel.ComposeToContactCommand.CanExecute(zoe).Should().BeTrue();
        viewModel.ComposeToContactCommand.CanExecute(adam).Should().BeFalse("it has no address");
        viewModel.ComposeToContactCommand.Execute(zoe);
        _launchProtocol.VerifySet(service => service.MailToUri = It.Is<MailToUri>(uri => uri != null), Times.Once);
    }

    [Fact]
    public async Task TitleBarSearch_FiltersTheLoadedFolderWithoutTheContactStore()
    {
        _favorites.Add(PublicFolderFavorite.Create(_exchange.Id, "staff", PublicFolderKind.Contacts, "Staff"));
        var viewModel = await NavigatedViewModelAsync();
        viewModel.SelectedFilter = PublicFolderEntries(viewModel).Single();
        await WaitUntilAsync(() => viewModel.Contacts.Count == 2 && !viewModel.IsLoading);

        var byCompany = await viewModel.SearchContactsAsync("fabrik", 6);
        var byAddress = await viewModel.SearchContactsAsync("ZOE@", 6);

        byCompany.Should().ContainSingle().Which.Name.Should().Be("Adam Apple");
        byAddress.Should().ContainSingle().Which.Id.Should().Be(viewModel.Contacts[1].Id, "a reload keeps the identity");
        _contactService.Verify(service => service.GetContactsPageAsync(
            It.Is<ContactQueryFilter>(filter => filter.SearchQuery != null),
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<ContactSortOrder>()), Times.Never);
    }

    [Fact]
    public async Task Unpinning_RemovesTheEntryAndFallsBackToAllContacts()
    {
        _favorites.Add(PublicFolderFavorite.Create(_exchange.Id, "staff", PublicFolderKind.Contacts, "Staff"));
        var viewModel = await NavigatedViewModelAsync();
        var entry = PublicFolderEntries(viewModel).Single();
        viewModel.SelectedFilter = entry;
        await WaitUntilAsync(() => viewModel.Contacts.Count == 2 && !viewModel.IsLoading);

        entry.UnpinCommand.Execute(null);
        ((CommunityToolkit.Mvvm.Messaging.IRecipient<PublicFolderFavoritesChanged>)viewModel)
            .Receive(new PublicFolderFavoritesChanged(_exchange.Id, PublicFolderKind.Contacts));
        await WaitUntilAsync(() => viewModel.Contacts.Count == 0 && !viewModel.IsLoading);

        _favoriteService.Verify(service => service.RemoveFavorite(_exchange.Id, "staff"), Times.Once);
        PublicFolderEntries(viewModel).Should().BeEmpty();
        viewModel.ShellMenu.Items.Should().NotContain(entry);
        viewModel.SelectedFilter.Kind.Should().Be(ContactFilterKind.All);
    }

    [Fact]
    public async Task PinningWhilePeopleIsOpen_AddsTheEntryAndKeepsTheSelection()
    {
        var viewModel = await NavigatedViewModelAsync();
        var selected = viewModel.SelectedFilter;
        _favorites.Add(PublicFolderFavorite.Create(_exchange.Id, "staff", PublicFolderKind.Contacts, "Staff"));

        ((CommunityToolkit.Mvvm.Messaging.IRecipient<PublicFolderFavoritesChanged>)viewModel)
            .Receive(new PublicFolderFavoritesChanged(_exchange.Id, PublicFolderKind.Calendar));
        PublicFolderEntries(viewModel).Should().BeEmpty("a calendar pin is not People's business");

        ((CommunityToolkit.Mvvm.Messaging.IRecipient<PublicFolderFavoritesChanged>)viewModel)
            .Receive(new PublicFolderFavoritesChanged(_exchange.Id, PublicFolderKind.Contacts));
        await WaitUntilAsync(() => PublicFolderEntries(viewModel).Count == 1);

        viewModel.SelectedFilter.Should().BeSameAs(selected);
    }

    private static List<ContactFilterViewModel> PublicFolderEntries(ContactsPageViewModel viewModel)
        => viewModel.FilterGroups.SelectMany(group => group).Where(filter => filter.IsPublicFolder).ToList();

    private async Task<ContactsPageViewModel> NavigatedViewModelAsync()
    {
        var accountService = new Mock<IAccountService>();
        accountService.Setup(service => service.GetAccountsAsync()).ReturnsAsync([_exchange, _imap]);

        var viewModel = new ContactsPageViewModel(
            _contactService.Object,
            accountService.Object,
            Mock.Of<ISynchronizationManager>(),
            _delegator.Object,
            Mock.Of<INavigationService>(),
            Mock.Of<IMailDialogService>(),
            _launchProtocol.Object,
            publicFolderService: _publicFolderService.Object,
            publicFolderFavoriteService: _favoriteService.Object)
        {
            Dispatcher = new ImmediateDispatcher()
        };

        viewModel.OnNavigatedTo(NavigationMode.New, null);
        await WaitUntilAsync(() => viewModel.ShellMenu?.Items.Count > 2 && viewModel.SelectedFilter != null && !viewModel.IsLoading);

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

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow.AddSeconds(3);
        while (!condition() && DateTime.UtcNow < timeout)
            await Task.Delay(10);

        condition().Should().BeTrue();
    }
}
