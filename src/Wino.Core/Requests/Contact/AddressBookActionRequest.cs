using System;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Messaging.UI;

namespace Wino.Core.Requests.Contact;

public sealed class AddressBookActionRequest : IContactActionRequest
{
    public AddressBookActionRequest(
        ContactSynchronizerOperation operation,
        ContactAddressBook addressBook,
        ContactAddressBook originalAddressBook = null)
    {
        if (operation is not ContactSynchronizerOperation.CreateAddressBook and
            not ContactSynchronizerOperation.RenameAddressBook and
            not ContactSynchronizerOperation.DeleteAddressBook)
            throw new ArgumentOutOfRangeException(nameof(operation));

        Operation = operation;
        AddressBook = Clone(addressBook) ?? throw new ArgumentNullException(nameof(addressBook));
        OriginalAddressBook = Clone(originalAddressBook);
    }

    public ContactAddressBook AddressBook { get; }
    public ContactAddressBook OriginalAddressBook { get; }
    public Guid LocalContactId => Guid.Empty;
    public Guid MailAccountId => AddressBook.MailAccountId;
    public Guid AddressBookId => AddressBook.Id;
    public ContactSourceKind SourceKind => ContactSourceKind.CardDav;
    public ContactSynchronizerOperation Operation { get; }
    public byte[] Photo => null;
    public int ResynchronizationDelay => 0;

    public object GroupingKey() => (MailAccountId, AddressBookId, Operation);

    public void ApplyUIChanges()
        => Send(AddressBook, Operation == ContactSynchronizerOperation.DeleteAddressBook
            ? OptimisticEntityChange.Delete
            : OptimisticEntityChange.Upsert, EntityUpdateSource.ClientUpdated);

    public void RevertUIChanges()
    {
        if (Operation == ContactSynchronizerOperation.CreateAddressBook)
        {
            Send(AddressBook, OptimisticEntityChange.Delete, EntityUpdateSource.ClientReverted);
            return;
        }

        Send(OriginalAddressBook ?? AddressBook, OptimisticEntityChange.Upsert, EntityUpdateSource.ClientReverted);
    }

    private static void Send(ContactAddressBook addressBook, OptimisticEntityChange change, EntityUpdateSource source)
        => WeakReferenceMessenger.Default.Send(new ContactAddressBookStateChanged(Clone(addressBook), change, source));

    private static ContactAddressBook Clone(ContactAddressBook source)
        => source is null ? null : new ContactAddressBook
        {
            Id = source.Id,
            MailAccountId = source.MailAccountId,
            SourceKind = source.SourceKind,
            RemoteId = source.RemoteId,
            ParentRemoteId = source.ParentRemoteId,
            DisplayName = source.DisplayName,
            IsDefault = source.IsDefault,
            IsReadOnly = source.IsReadOnly,
            IsPendingRemoteOperation = source.IsPendingRemoteOperation,
            DeltaToken = source.DeltaToken,
            LastSuccessfulSyncUtc = source.LastSuccessfulSyncUtc
        };
}
