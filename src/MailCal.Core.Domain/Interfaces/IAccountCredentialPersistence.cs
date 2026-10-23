using System;
using System.Threading;
using System.Threading.Tasks;
using MailCal.Core.Domain.Entities.Shared;

namespace MailCal.Core.Domain.Interfaces;

/// <summary>Host-selected persistence policy. Prepared entities alone may be written to the database.</summary>
public interface IAccountCredentialPersistence
{
    Task<CustomServerInformation> PrepareServerInformationForStorageAsync(CustomServerInformation information, CancellationToken cancellationToken = default);
    Task RestoreServerInformationSecretsAsync(CustomServerInformation information, CancellationToken cancellationToken = default);
    Task DeleteMailAccountSecretsAsync(Guid accountId, CancellationToken cancellationToken = default);
}
