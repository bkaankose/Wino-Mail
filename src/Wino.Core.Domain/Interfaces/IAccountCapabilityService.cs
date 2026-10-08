using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Core.Domain.Interfaces;

public interface IAccountCapabilityService
{
    Task<MailAccount> ApplyAsync(
        MailAccount account,
        bool includeMail,
        bool includeCalendar,
        bool includeContacts,
        bool includeTasks,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates the local task list and address book that an account's enabled local To Do and
    /// contacts need. Existing stores are kept, so calling it again changes nothing.
    /// </summary>
    Task EnsureLocalCapabilityStoresAsync(MailAccount account, CancellationToken cancellationToken = default);
}
