using System;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Interfaces;

public interface IProviderFeatureAuthorizationService
{
    bool IsSupported(MailAccount account, ProviderFeature feature);
    Task<AccountProviderFeature> GetFeatureAsync(Guid accountId, ProviderFeature feature, CancellationToken cancellationToken = default);
    Task EnableAsync(Guid accountId, ProviderFeature feature, CancellationToken cancellationToken = default);
    Task DisableAsync(Guid accountId, ProviderFeature feature, CancellationToken cancellationToken = default);
    Task MarkReauthorizationRequiredAsync(Guid accountId, ProviderFeature feature, CancellationToken cancellationToken = default);
}
