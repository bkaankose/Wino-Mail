using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Messaging.UI;

namespace Wino.Core.Requests.Contact;

public enum ApplicationLocalContactOperation
{
    SetFavorite,
    CreateList,
    UpdateList,
    DeleteList,
    AddMembership,
    RemoveMembership,
    SetMemberships
}

public sealed class ApplicationLocalContactRequest : IRequestBase
{
    public ApplicationLocalContactRequest(
        ApplicationLocalContactOperation operation,
        AccountContact contact = null,
        AccountContact originalContact = null,
        ContactList list = null,
        ContactList originalList = null,
        IEnumerable<Guid> contactIds = null,
        IEnumerable<Guid> desiredListIds = null,
        IEnumerable<Guid> originalListIds = null)
    {
        Operation = operation;
        Contact = RequestEntityCloner.Contact(contact);
        OriginalContact = RequestEntityCloner.Contact(originalContact);
        List = RequestEntityCloner.ContactList(list);
        OriginalList = RequestEntityCloner.ContactList(originalList);
        ContactIds = contactIds?.Where(id => id != Guid.Empty).Distinct().ToArray() ?? [];
        DesiredListIds = desiredListIds?.Where(id => id != Guid.Empty).Distinct().ToArray() ?? [];
        OriginalListIds = originalListIds?.Where(id => id != Guid.Empty).Distinct().ToArray() ?? [];
    }

    public ApplicationLocalContactOperation Operation { get; }
    public AccountContact Contact { get; }
    public AccountContact OriginalContact { get; }
    public ContactList List { get; }
    public ContactList OriginalList { get; }
    public IReadOnlyList<Guid> ContactIds { get; }
    public IReadOnlyList<Guid> DesiredListIds { get; }
    public IReadOnlyList<Guid> OriginalListIds { get; }
    public int ResynchronizationDelay => 0;

    public object GroupingKey()
        => (Operation, Contact?.Id, List?.Id);

    public void ApplyUIChanges()
        => Publish(revert: false);

    public void RevertUIChanges()
        => Publish(revert: true);

    private void Publish(bool revert)
    {
        switch (Operation)
        {
            case ApplicationLocalContactOperation.SetFavorite:
                var contact = revert ? OriginalContact : Contact;
                if (contact is not null)
                    WeakReferenceMessenger.Default.Send(new ContactStateChanged(
                        contact,
                        OptimisticEntityChange.Upsert,
                        revert ? EntityUpdateSource.ClientReverted : EntityUpdateSource.ClientUpdated));
                break;
            case ApplicationLocalContactOperation.CreateList:
                PublishList(List, revert ? OptimisticEntityChange.Delete : OptimisticEntityChange.Upsert, revert);
                break;
            case ApplicationLocalContactOperation.UpdateList:
                PublishList(revert ? OriginalList : List, OptimisticEntityChange.Upsert, revert);
                break;
            case ApplicationLocalContactOperation.DeleteList:
                PublishList(revert ? OriginalList : List, revert ? OptimisticEntityChange.Upsert : OptimisticEntityChange.Delete, revert);
                break;
            case ApplicationLocalContactOperation.AddMembership:
            case ApplicationLocalContactOperation.RemoveMembership:
                var added = Operation == ApplicationLocalContactOperation.AddMembership;
                WeakReferenceMessenger.Default.Send(new ContactListMembershipStateChanged(
                    List.Id,
                    ContactIds,
                    revert ? !added : added,
                    revert ? EntityUpdateSource.ClientReverted : EntityUpdateSource.ClientUpdated));
                break;
            case ApplicationLocalContactOperation.SetMemberships:
                break;
        }
    }

    private static void PublishList(ContactList list, OptimisticEntityChange change, bool revert)
    {
        if (list is not null)
            WeakReferenceMessenger.Default.Send(new ContactListStateChanged(
                list,
                change,
                revert ? EntityUpdateSource.ClientReverted : EntityUpdateSource.ClientUpdated));
    }
}
