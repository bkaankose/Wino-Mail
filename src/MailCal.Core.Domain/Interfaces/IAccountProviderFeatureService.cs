using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MailCal.Core.Domain.Entities.Shared;
using MailCal.Core.Domain.Enums;

namespace MailCal.Core.Domain.Interfaces;

public interface IAccountProviderFeatureService
{
    Task<IReadOnlyList<AccountProviderFeature>> GetFeaturesAsync(Guid accountId, CancellationToken cancellationToken = default);
    Task<AccountProviderFeature> GetFeatureAsync(Guid accountId, ProviderFeature feature, CancellationToken cancellationToken = default);
    Task<bool> IsEnabledAsync(Guid accountId, ProviderFeature feature, CancellationToken cancellationToken = default);
    Task UpsertAsync(AccountProviderFeature feature, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid accountId, ProviderFeature feature, CancellationToken cancellationToken = default);
    Task DeleteForAccountAsync(Guid accountId, CancellationToken cancellationToken = default);
}
