using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Accounts;

namespace Wino.Core.Domain.Interfaces;

public interface IBaseSynchronizer
{
    /// <summary>
    /// Account that is assigned for this synchronizer.
    /// </summary>
    MailAccount Account { get; }

    /// <summary>
    /// Synchronizer state.
    /// </summary>
    AccountSynchronizerState State { get; }

    /// <summary>
    /// Queues a single request to be executed in the next synchronization.
    /// </summary>
    /// <param name="request">Request to queue.</param>
    void QueueRequest(IRequestBase request);

    /// <summary>
    /// Synchronizes profile information with the server.
    /// Sender name and Profile picture are updated.
    /// </summary>
    /// <returns>Profile information model that holds the values.</returns>
    Task<ProfileInformation> GetProfileInformationAsync();
}
