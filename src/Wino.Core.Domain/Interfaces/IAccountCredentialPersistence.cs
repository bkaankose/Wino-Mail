#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Core.Domain.Interfaces;

/// <summary>Host-selected persistence policy. Prepared entities alone may be written to the database.</summary>
public interface IAccountCredentialPersistence
{
    Task<CustomServerInformation> PrepareServerInformationForStorageAsync(CustomServerInformation information, CancellationToken cancellationToken = default);
    Task RestoreServerInformationSecretsAsync(CustomServerInformation information, CancellationToken cancellationToken = default);
    Task<WinoAccount> PrepareWinoAccountForStorageAsync(WinoAccount account, CancellationToken cancellationToken = default);
    Task RestoreWinoAccountSecretsAsync(WinoAccount account, CancellationToken cancellationToken = default);
    Task DeleteMailAccountSecretsAsync(Guid accountId, CancellationToken cancellationToken = default);
    Task DeleteWinoAccountSecretsAsync(Guid accountId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the stored secrets a database update replaced. Both values are prepared column values; anything but a
    /// storage reference that differs from <paramref name="currentStoredValue"/> is ignored. Failures are logged, not thrown.
    /// </summary>
    Task DeleteSupersededSecretsAsync(Guid accountId, string? previousStoredValue, string? currentStoredValue, CancellationToken cancellationToken = default);
}
