using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Core.Domain.Interfaces;

public interface IMailFilterProviderService
{
    bool SupportsProviderFilters(MailAccount account);
    Task<bool> IsProviderFiltersEnabledAsync(Guid accountId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MailFilter>> GetFiltersAsync(MailAccount account, CancellationToken cancellationToken = default);
    Task<MailFilter> CreateFilterAsync(MailAccount account, MailFilter filter, CancellationToken cancellationToken = default);
    Task UpdateFilterAsync(MailAccount account, MailFilter filter, CancellationToken cancellationToken = default);
    Task DeleteFilterAsync(MailAccount account, MailFilter filter, CancellationToken cancellationToken = default);
}
