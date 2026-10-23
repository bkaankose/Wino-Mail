using System;
using System.Threading;
using System.Threading.Tasks;
using MailCal.Core.Domain.Entities.Shared;

namespace MailCal.Core.Domain.Interfaces;

public interface ICardDavAddressBookService
{
    Task<ContactAddressBook> CreateAsync(Guid accountId, string displayName, CancellationToken cancellationToken = default);
    Task RenameAsync(Guid addressBookId, string displayName, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid addressBookId, bool destructiveConfirmation, CancellationToken cancellationToken = default);
}
