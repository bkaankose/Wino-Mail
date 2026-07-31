using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Mail;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Provider-specific mail rule operations exposed by synchronizers that own an
/// authenticated provider client.
/// </summary>
public interface IProviderMailFilterSynchronizer
{
    Task<IReadOnlyList<MailFilter>> GetProviderFiltersAsync(CancellationToken cancellationToken = default);
    Task<MailFilter> CreateProviderFilterAsync(MailFilter filter, CancellationToken cancellationToken = default);
    Task<MailFilter> UpdateProviderFilterAsync(MailFilter filter, CancellationToken cancellationToken = default);
    Task DeleteProviderFilterAsync(string remoteId, CancellationToken cancellationToken = default);
}
