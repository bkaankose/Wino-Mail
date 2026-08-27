using System;
using System.Collections.Generic;
using FluentAssertions;
using Moq;
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
}
