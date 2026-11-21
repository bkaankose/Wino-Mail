using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;

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

    /// <summary>
    /// Turns one app of the account off, or on with a source, for every provider. A server source is signed in to
    /// and synchronized once before the account is saved, so nothing is written unless the server answered. Turning
    /// an app off removes its data from this device; leaving a server removes only the synced copies. Items kept on
    /// this device stay when the app moves to a server and are not uploaded. Returns the stored account.
    /// </summary>
    Task<MailAccount> SetAppModeAsync(
        MailAccount account,
        WinoApplicationMode app,
        AccountCapabilityMode mode,
        CancellationToken cancellationToken = default);
}
