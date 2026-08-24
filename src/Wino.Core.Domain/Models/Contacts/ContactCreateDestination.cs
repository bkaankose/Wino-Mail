using System;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Contacts;

public record ContactCreateDestination(
    Guid MailAccountId,
    Guid AddressBookId,
    ContactSourceKind SourceKind,
    string AccountName,
    string AddressBookName,
    bool IsDefault)
{
    public string DisplayName => $"{AccountName} · {AddressBookName}";
}
