using System;
using System.Collections.Generic;
using FluentAssertions;
using Moq;
using System.Linq;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Requests.Contact;
using Wino.Core.Synchronizers;
using Xunit;

namespace Wino.Core.Tests.Synchronizers;

public sealed class LocalContactSynchronizerTests
{
    [Fact]
    public async Task ExecuteRequests_CompletesLocalMutationsWithoutProviderClient()
    {
        var contactService = new Mock<IContactService>();
        contactService.Setup(service => service.CompleteMutationAsync(
                It.IsAny<Guid>(),
                It.IsAny<AccountContact>(),
                It.IsAny<bool>()))
            .Returns(Task.CompletedTask);
        var synchronizer = new LocalContactSynchronizer(contactService.Object);
        var contact = new AccountContact
        {
            Id = Guid.NewGuid(), MailAccountId = Guid.NewGuid(), AddressBookId = Guid.NewGuid(),
            SourceKind = ContactSourceKind.Local
        };
        IReadOnlyList<IContactActionRequest> requests =
        [
            new ContactActionRequest(contact, ContactSynchronizerOperation.Create),
            new ContactActionRequest(contact, ContactSynchronizerOperation.Update),
            new ContactActionRequest(contact, ContactSynchronizerOperation.Delete)
        ];

        var action = async () => await synchronizer.ExecuteRequestsAsync(requests, default);

        await action.Should().NotThrowAsync();
        contactService.Verify(service => service.CompleteMutationAsync(
            contact.Id,
            It.IsAny<AccountContact>(),
            It.IsAny<bool>()), Times.Exactly(3));
        (await synchronizer.SynchronizeAsync(new ContactSynchronizationOptions { AccountId = contact.MailAccountId }, default))
            .CompletedState.Should().Be(SynchronizationCompletedState.Success);
    }

    [Fact]
    public async Task ExecuteRequests_StoresCategoriesOnTheDevice()
    {
        var contactService = new Mock<IContactService>();
        var synchronizer = new LocalContactSynchronizer(contactService.Object);
        var contact = new AccountContact
        {
            Id = Guid.NewGuid(), MailAccountId = Guid.NewGuid(), AddressBookId = Guid.NewGuid(),
            SourceKind = ContactSourceKind.Local
        };
        var request = new ContactCategoryRequest(contact, [new MailCategory { Id = Guid.NewGuid(), Name = "Family" }]);

        await synchronizer.ExecuteRequestsAsync([request], default);

        request.Operation.Should().Be(ContactSynchronizerOperation.UpdateCategories);
        contactService.Verify(service => service.SetContactCategoriesAsync(
            contact.Id,
            It.Is<IEnumerable<string>>(names => names.SequenceEqual(new[] { "Family" }))), Times.Once);
        contactService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ExecuteRequests_StoresListChangesOnTheDevice()
    {
        var contactService = new Mock<IContactService>();
        var synchronizer = new LocalContactSynchronizer(contactService.Object);
        var contactId = Guid.NewGuid();
        var list = new ContactList { Name = "Family", MailAccountId = Guid.NewGuid(), AddressBookId = Guid.NewGuid() };
        IReadOnlyList<IContactActionRequest> requests =
        [
            new ContactListRequest(ContactSynchronizerOperation.CreateList, list),
            new ContactListRequest(ContactSynchronizerOperation.UpdateListMembers, list, addedContactIds: [contactId]),
            new ContactListRequest(ContactSynchronizerOperation.RenameList, list),
            new ContactListRequest(ContactSynchronizerOperation.DeleteList, list)
        ];

        await synchronizer.ExecuteRequestsAsync(requests, default);

        contactService.Verify(service => service.SaveContactListAsync(It.Is<ContactList>(item => item.Id == list.Id)), Times.Once);
        contactService.Verify(service => service.AddContactsToListAsync(list.Id, It.Is<IEnumerable<Guid>>(ids => ids.Single() == contactId)), Times.Once);
        contactService.Verify(service => service.RemoveContactsFromListAsync(list.Id, It.Is<IEnumerable<Guid>>(ids => !ids.Any())), Times.Once);
        contactService.Verify(service => service.UpdateContactListAsync(It.Is<ContactList>(item => item.Id == list.Id)), Times.Once);
        contactService.Verify(service => service.DeleteContactListAsync(list.Id), Times.Once);
        contactService.VerifyNoOtherCalls();
    }
}
