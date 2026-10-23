using System;
using MailCal.Core.Domain.Enums;

namespace MailCal.Core.Domain.Models.Contacts;

public record ContactCreateDestination(
    Guid MailAccountId,
    Guid AddressBookId,
    ContactSourceKind SourceKind,
    string AccountName,
    string AddressBookName,
    bool IsDefault,
    bool IsReadOnly = false)
{
    public string DisplayName => $"{AccountName} · {AddressBookName}";
}
