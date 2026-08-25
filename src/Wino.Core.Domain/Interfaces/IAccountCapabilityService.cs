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
}
