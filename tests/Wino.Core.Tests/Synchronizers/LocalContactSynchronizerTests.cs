using System;
using System.Collections.Generic;
using FluentAssertions;
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
        var synchronizer = new LocalContactSynchronizer();
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
        (await synchronizer.SynchronizeAsync(new ContactSynchronizationOptions { AccountId = contact.MailAccountId }, default))
            .CompletedState.Should().Be(SynchronizationCompletedState.Success);
    }
}
