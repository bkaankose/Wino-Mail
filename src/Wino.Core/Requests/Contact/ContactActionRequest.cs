using System;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Requests.Contact;

public record ContactActionRequest(
    AccountContact Contact,
    ContactSynchronizerOperation Operation,
    AccountContact OriginalContact = null,
    byte[] Photo = null) : IContactActionRequest
{
    public Guid LocalContactId => Contact.Id;
    public Guid MailAccountId => Contact.MailAccountId;
    public Guid AddressBookId => Contact.AddressBookId;
    public ContactSourceKind SourceKind => Contact.SourceKind;
    public int ResynchronizationDelay => 0;
    public object GroupingKey() => (AddressBookId, LocalContactId, Operation);
    public void ApplyUIChanges() { }
    public void RevertUIChanges() { }
}
