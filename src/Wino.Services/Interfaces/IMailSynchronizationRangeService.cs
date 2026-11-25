using System;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Synchronization;

namespace Wino.Services.Interfaces;

/// <summary>Widens an account's mail synchronization range after its first synchronization.</summary>
public interface IMailSynchronizationRangeService
{
    Task<MailSynchronizationRangeStatus> GetStatusAsync(Guid accountId);

    /// <summary>
    /// Downloads the older mail and stores the wider range only when every folder finished. Returns false when the
    /// change was refused (unsupported, not wider, first synchronization not done, already running) or failed.
    /// </summary>
    Task<bool> ChangeRangeAsync(Guid accountId, InitialSynchronizationRange target, CancellationToken cancellationToken = default);
}
