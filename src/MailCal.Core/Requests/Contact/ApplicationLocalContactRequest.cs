using CommunityToolkit.Mvvm.Messaging;
using MailCal.Core.Domain.Entities.Shared;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Interfaces;
using MailCal.Core.Domain.Models.Requests;
using MailCal.Messaging.UI;

namespace MailCal.Core.Requests.Contact;

public enum ApplicationLocalContactOperation
{
    SetFavorite
}

/// <summary>
/// A change to contact data no provider knows about. Favorites are the only one: they
/// exist in Mail-Cal alone, for every kind of account.
/// </summary>
public sealed class ApplicationLocalContactRequest : IRequestBase
{
    public ApplicationLocalContactRequest(
        ApplicationLocalContactOperation operation,
        AccountContact contact = null,
        AccountContact originalContact = null)
    {
        Operation = operation;
        Contact = RequestEntityCloner.Contact(contact);
        OriginalContact = RequestEntityCloner.Contact(originalContact);
    }

    public ApplicationLocalContactOperation Operation { get; }
    public AccountContact Contact { get; }
    public AccountContact OriginalContact { get; }
    public int ResynchronizationDelay => 0;
    public RequestTrace Trace { get; set; }

    public object GroupingKey()
        => (Operation, Contact?.Id);

    public void ApplyUIChanges()
        => Publish(revert: false);

    public void RevertUIChanges()
        => Publish(revert: true);

    private void Publish(bool revert)
    {
        var contact = revert ? OriginalContact : Contact;
        if (contact is not null)
            WeakReferenceMessenger.Default.Send(new ContactStateChanged(
                contact,
                OptimisticEntityChange.Upsert,
                revert ? EntityUpdateSource.ClientReverted : EntityUpdateSource.ClientUpdated));
    }
}
