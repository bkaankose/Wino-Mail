using System;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Messaging.UI;

namespace Wino.Core.Requests.Contact;

/// <summary>
/// Carries immutable desired and original snapshots for one contact provider operation.
/// UI messages never persist either snapshot; the synchronizer commits only after HTTP succeeds.
/// </summary>
public sealed record ContactActionRequest : IContactActionRequest
{
    public ContactActionRequest(
        AccountContact Contact,
        ContactSynchronizerOperation Operation,
        AccountContact OriginalContact = null,
        byte[] Photo = null)
    {
        this.Contact = RequestEntityCloner.Contact(Contact) ?? throw new ArgumentNullException(nameof(Contact));
        this.OriginalContact = RequestEntityCloner.Contact(OriginalContact);
        this.Operation = Operation;
        this.Photo = Photo is null ? null : (byte[])Photo.Clone();
    }

    public AccountContact Contact { get; }
    public AccountContact OriginalContact { get; }
    public ContactSynchronizerOperation Operation { get; }
    public byte[] Photo { get; }
    public Guid LocalContactId => Contact.Id;
    public Guid MailAccountId => Contact.MailAccountId;
    public Guid AddressBookId => Contact.AddressBookId;
    public ContactSourceKind SourceKind => Contact.SourceKind;
    public int ResynchronizationDelay => 0;

    public object GroupingKey() => (AddressBookId, LocalContactId, Operation);

    public void ApplyUIChanges()
        => Send(Contact, Operation == ContactSynchronizerOperation.Delete
            ? OptimisticEntityChange.Delete
            : OptimisticEntityChange.Upsert, EntityUpdateSource.ClientUpdated);

    public void RevertUIChanges()
    {
        if (Operation == ContactSynchronizerOperation.Create)
        {
            Send(Contact, OptimisticEntityChange.Delete, EntityUpdateSource.ClientReverted);
            return;
        }

        Send(OriginalContact ?? Contact, OptimisticEntityChange.Upsert, EntityUpdateSource.ClientReverted);
    }

    private static void Send(AccountContact contact, OptimisticEntityChange change, EntityUpdateSource source)
        => WeakReferenceMessenger.Default.Send(new ContactStateChanged(
            RequestEntityCloner.Contact(contact),
            change,
            source));
}
