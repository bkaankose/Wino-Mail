using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Requests;
using Wino.Messaging.UI;

namespace Wino.Core.Requests.Contact;

/// <summary>
/// Creates, renames or deletes a contact list, or changes who is in it. The synchronizer
/// of the owning account stores the change where the list lives: on the device, as a
/// CardDAV group or as a Google contact group.
/// </summary>
public sealed record ContactListRequest : IContactActionRequest
{
    public ContactListRequest(
        ContactSynchronizerOperation operation,
        ContactList list,
        ContactList originalList = null,
        IEnumerable<Guid> addedContactIds = null,
        IEnumerable<Guid> removedContactIds = null)
    {
        if (operation is not (ContactSynchronizerOperation.CreateList or ContactSynchronizerOperation.RenameList
            or ContactSynchronizerOperation.DeleteList or ContactSynchronizerOperation.UpdateListMembers))
        {
            throw new ArgumentOutOfRangeException(nameof(operation), operation, "The operation is not a contact list operation.");
        }

        Operation = operation;
        List = RequestEntityCloner.ContactList(list) ?? throw new ArgumentNullException(nameof(list));
        OriginalList = RequestEntityCloner.ContactList(originalList);
        AddedContactIds = addedContactIds?.Where(id => id != Guid.Empty).Distinct().ToArray() ?? [];
        RemovedContactIds = removedContactIds?.Where(id => id != Guid.Empty).Distinct().ToArray() ?? [];
    }

    public ContactSynchronizerOperation Operation { get; }
    public ContactList List { get; }
    public ContactList OriginalList { get; }
    public IReadOnlyList<Guid> AddedContactIds { get; }
    public IReadOnlyList<Guid> RemovedContactIds { get; }
    public Guid LocalContactId => Guid.Empty;
    public Guid MailAccountId => List.MailAccountId ?? Guid.Empty;
    public Guid AddressBookId => List.AddressBookId ?? Guid.Empty;
    public ContactSourceKind SourceKind => ContactSourceKind.Local;
    public byte[] Photo => null;
    public int ResynchronizationDelay => 0;
    public RequestTrace Trace { get; set; }

    public object GroupingKey() => (AddressBookId, List.Id, Operation);

    public void ApplyUIChanges() => Publish(revert: false);

    public void RevertUIChanges() => Publish(revert: true);

    /// <summary>Stores an accepted change in the local database.</summary>
    public async Task CommitAsync(IContactService contactService)
    {
        switch (Operation)
        {
            case ContactSynchronizerOperation.CreateList:
                await contactService.SaveContactListAsync(List).ConfigureAwait(false);
                break;
            case ContactSynchronizerOperation.RenameList:
                await contactService.UpdateContactListAsync(List).ConfigureAwait(false);
                break;
            case ContactSynchronizerOperation.DeleteList:
                await contactService.DeleteContactListAsync(List.Id).ConfigureAwait(false);
                break;
            case ContactSynchronizerOperation.UpdateListMembers:
                await contactService.AddContactsToListAsync(List.Id, AddedContactIds).ConfigureAwait(false);
                await contactService.RemoveContactsFromListAsync(List.Id, RemovedContactIds).ConfigureAwait(false);
                break;
        }
    }

    private void Publish(bool revert)
    {
        var source = revert ? EntityUpdateSource.ClientReverted : EntityUpdateSource.ClientUpdated;

        switch (Operation)
        {
            case ContactSynchronizerOperation.CreateList:
                Send(List, revert ? OptimisticEntityChange.Delete : OptimisticEntityChange.Upsert, source);
                break;
            case ContactSynchronizerOperation.RenameList:
                Send(revert ? OriginalList ?? List : List, OptimisticEntityChange.Upsert, source);
                break;
            case ContactSynchronizerOperation.DeleteList:
                Send(revert ? OriginalList ?? List : List, revert ? OptimisticEntityChange.Upsert : OptimisticEntityChange.Delete, source);
                break;
            case ContactSynchronizerOperation.UpdateListMembers:
                if (AddedContactIds.Count > 0)
                    WeakReferenceMessenger.Default.Send(new ContactListMembershipStateChanged(List.Id, AddedContactIds, !revert, source));
                if (RemovedContactIds.Count > 0)
                    WeakReferenceMessenger.Default.Send(new ContactListMembershipStateChanged(List.Id, RemovedContactIds, revert, source));
                break;
        }
    }

    private static void Send(ContactList list, OptimisticEntityChange change, EntityUpdateSource source)
        => WeakReferenceMessenger.Default.Send(new ContactListStateChanged(RequestEntityCloner.ContactList(list), change, source));
}
