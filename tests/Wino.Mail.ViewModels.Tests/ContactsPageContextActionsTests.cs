using FluentAssertions;
using Moq;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Contacts;
using Wino.Core.Requests.Contact;
using Wino.Mail.ViewModels.Data;
using Xunit;

namespace Wino.Mail.ViewModels.Tests;

public class ContactsPageContextActionsTests
{
    private readonly Mock<IContactQueryService> _queries = new();
    private readonly Mock<IWinoRequestDelegator> _requests = new();
    private readonly Mock<IMailDialogService> _dialogs = new();
    private readonly Mock<IActivationStateService> _activation = new();

    private ContactsPageViewModel CreateViewModel() => new(
        _queries.Object, Mock.Of<IAccountService>(), Mock.Of<ISynchronizationManager>(),
        _requests.Object, Mock.Of<INavigationService>(), _dialogs.Object, _activation.Object);

    private static AccountContactViewModel Contact(bool editable = true, bool favorite = false, string? email = null)
        => new(new AccountContact
        {
            Id = Guid.NewGuid(), DisplayName = "Test contact", IsFavorite = favorite,
            SourceKind = editable ? ContactSourceKind.Local : ContactSourceKind.CardDav,
            EmailAddresses = email is null ? [] : [new() { Address = email }]
        }, isAuthorized: editable);

    [Fact]
    public void ContextTargets_UseSelectionOnlyWhenOpenedOnASelectedContact_AndCaptureSnapshot()
    {
        var vm = CreateViewModel();
        var first = Contact();
        var second = Contact();
        var outside = Contact();
        vm.SelectedContacts.Add(first);
        vm.SelectedContacts.Add(second);
        vm.SelectedContacts.Add(first);

        var targets = vm.ResolveContactContextTargets(first);
        targets.Should().Equal(first, second);
        vm.ResolveContactContextTargets(outside).Should().Equal(outside);
        vm.SelectedContacts.Clear();
        targets.Should().Equal(first, second);
        vm.ResolveContactContextTargets(first).Should().Equal(first);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Delete_ConfirmsActualEditableCount_AndQueuesOneBatchOnlyAfterConfirmation(bool confirmed)
    {
        var vm = CreateViewModel();
        var first = Contact();
        var second = Contact();
        var readOnly = Contact(editable: false);
        _dialogs.Setup(d => d.ShowConfirmationDialogAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(confirmed);
        _requests.Setup(r => r.ExecuteAsync(It.IsAny<IReadOnlyList<ContactOperationPreparationRequest>>()))
            .Returns(Task.CompletedTask);

        await vm.DeleteContactsAsync(new[] { first, second, first, readOnly });

        _dialogs.Verify(d => d.ShowConfirmationDialogAsync(
            string.Format(Translator.ContactConfirmDialog_DeleteMultipleMessage, 2),
            Translator.ContactsPage_DeleteSelectedContacts, Translator.ContactConfirmDialog_DeleteButton), Times.Once);
        _requests.Verify(r => r.ExecuteAsync(It.Is<IReadOnlyList<ContactOperationPreparationRequest>>(requests =>
            requests.Count == 2 && requests[0].Contact.Id == first.Id && requests[1].Contact.Id == second.Id)),
            confirmed ? Times.Once() : Times.Never());
    }

    [Fact]
    public async Task Delete_UsesNamedSingleContactMessageWhenOnlyOneIsEditable()
    {
        var vm = CreateViewModel();
        var contact = Contact();
        await vm.DeleteContactsAsync(new[] { contact, Contact(editable: false) });
        _dialogs.Verify(d => d.ShowConfirmationDialogAsync(
            string.Format(Translator.ContactConfirmDialog_DeleteMessage, contact.SourceContact.DisplayValue),
            Translator.ContactConfirmDialog_DeleteTitle, Translator.ContactConfirmDialog_DeleteButton), Times.Once);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Favorite_AppliesOneStateToWholeSelection(bool firstFavorite, bool expected)
    {
        var vm = CreateViewModel();
        var first = Contact(favorite: firstFavorite);
        var second = Contact(favorite: true);
        _requests.Setup(r => r.ExecuteLocalAsync(It.IsAny<IRequestBase>())).Returns(Task.CompletedTask);

        await vm.FavoriteContactsAsync(new[] { first, second, first });

        _requests.Verify(r => r.ExecuteLocalAsync(It.Is<IRequestBase>(r =>
            r is ApplicationLocalContactRequest && ((ApplicationLocalContactRequest)r).Contact.IsFavorite == expected)), Times.Exactly(2));
    }

    [Fact]
    public void Compose_IncludesAllAvailableAddressesWithoutDuplicates()
    {
        var vm = CreateViewModel();
        vm.ComposeToContacts(new[] { Contact(email: "one@example.com"), Contact(),
            Contact(email: "two@example.com"), Contact(email: "ONE@example.com") });
        _activation.VerifySet(a => a.MailToUri = It.Is<Wino.Core.Domain.Models.Launch.MailToUri>(uri =>
            uri.To.SequenceEqual(new[] { "one@example.com", "two@example.com" })), Times.Once);
    }

    [Fact]
    public async Task AssignableLists_ExcludeOnlyListsAlreadyContainingEveryTarget()
    {
        var vm = CreateViewModel();
        var first = Contact();
        var second = Contact();
        var shared = new ContactList { Id = Guid.NewGuid(), Name = "Shared" };
        var partial = new ContactList { Id = Guid.NewGuid(), Name = "Partial" };
        vm.ContactLists.Add(shared);
        vm.ContactLists.Add(partial);
        _queries.Setup(q => q.GetListIdsForContactAsync(first.Id)).ReturnsAsync(new List<Guid> { shared.Id, partial.Id });
        _queries.Setup(q => q.GetListIdsForContactAsync(second.Id)).ReturnsAsync(new List<Guid> { shared.Id });

        (await vm.GetAssignableListsAsync(new[] { first, second })).Should().Equal(partial);
        await vm.AssignContactsToListAsync(partial, new[] { first.Id, second.Id });
        _requests.Verify(r => r.ExecuteLocalAsync(It.Is<IRequestBase>(r =>
            r is ApplicationLocalContactRequest && ((ApplicationLocalContactRequest)r).ContactIds.Count == 2)), Times.Once);
    }
}
