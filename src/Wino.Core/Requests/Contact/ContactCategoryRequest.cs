using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Requests;
using Wino.Messaging.UI;

namespace Wino.Core.Requests.Contact;

/// <summary>
/// Makes one contact carry exactly the given categories. The synchronizer of the account
/// writes them where the contact lives: on the device, on the Outlook contact or on the
/// CardDAV card.
/// </summary>
public sealed record ContactCategoryRequest : IContactActionRequest
{
    public ContactCategoryRequest(AccountContact contact, IEnumerable<MailCategory> categories)
    {
        OriginalContact = RequestEntityCloner.Contact(contact) ?? throw new ArgumentNullException(nameof(contact));
        Contact = RequestEntityCloner.Contact(contact);
        Contact.Categories = categories?.Select(RequestEntityCloner.MailCategory).ToList() ?? [];
    }

    public AccountContact Contact { get; }
    public AccountContact OriginalContact { get; }
    public IReadOnlyList<string> CategoryNames => [.. Contact.Categories.Select(category => category.Name)];
    public ContactSynchronizerOperation Operation => ContactSynchronizerOperation.UpdateCategories;
    public byte[] Photo => null;
    public Guid LocalContactId => Contact.Id;
    public Guid MailAccountId => Contact.MailAccountId;
    public Guid AddressBookId => Contact.AddressBookId;
    public ContactSourceKind SourceKind => Contact.SourceKind;
    public int ResynchronizationDelay => 0;
    public RequestTrace Trace { get; set; }

    public object GroupingKey() => (AddressBookId, LocalContactId, Operation);

    public void ApplyUIChanges() => Send(Contact, EntityUpdateSource.ClientUpdated);

    public void RevertUIChanges() => Send(OriginalContact, EntityUpdateSource.ClientReverted);

    private static void Send(AccountContact contact, EntityUpdateSource source)
        => WeakReferenceMessenger.Default.Send(new ContactStateChanged(
            RequestEntityCloner.Contact(contact),
            OptimisticEntityChange.Upsert,
            source));
}
