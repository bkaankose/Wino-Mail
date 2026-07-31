using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Mail;

namespace Wino.Core.Domain.Interfaces;

public interface IMailFilterService
{
    Task<List<MailFilter>> GetFiltersAsync(Guid accountId, CancellationToken cancellationToken = default);
    Task<MailFilter> GetFilterAsync(Guid filterId, CancellationToken cancellationToken = default);
    Task<MailFilter> CreateFilterAsync(MailFilter filter, CancellationToken cancellationToken = default);
    Task UpdateFilterAsync(MailFilter filter, CancellationToken cancellationToken = default);
    Task DeleteFilterAsync(Guid filterId, CancellationToken cancellationToken = default);
    Task DeleteFiltersForAccountAsync(Guid accountId, CancellationToken cancellationToken = default);
    Task ReplaceProviderFiltersAsync(Guid accountId, IReadOnlyCollection<MailFilter> providerFilters, CancellationToken cancellationToken = default);
    Task<List<MailFilter>> GetExecutableFiltersAsync(Guid accountId, string sourceRemoteFolderId, CancellationToken cancellationToken = default);
    Task<bool> HasExecutionAsync(Guid filterId, string remoteMessageId, string sourceRemoteFolderId, CancellationToken cancellationToken = default);
    Task CreateExecutionAsync(MailFilterExecution execution, CancellationToken cancellationToken = default);
}
