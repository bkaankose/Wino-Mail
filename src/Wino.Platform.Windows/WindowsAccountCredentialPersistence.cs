using System;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;

namespace Wino.Platform.Windows;

/// <summary>Preserves the existing Windows SQLite representation without a credential migration.</summary>
public sealed class WindowsAccountCredentialPersistence : IAccountCredentialPersistence
{
    public Task<CustomServerInformation> PrepareServerInformationForStorageAsync(CustomServerInformation information, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(information);
    }

    public Task<WinoAccount> PrepareWinoAccountForStorageAsync(WinoAccount account, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(account);
    }

    public Task RestoreServerInformationSecretsAsync(CustomServerInformation information, CancellationToken cancellationToken = default) => Complete(cancellationToken);
    public Task RestoreWinoAccountSecretsAsync(WinoAccount account, CancellationToken cancellationToken = default) => Complete(cancellationToken);
    public Task DeleteMailAccountSecretsAsync(Guid accountId, CancellationToken cancellationToken = default) => Complete(cancellationToken);
    public Task DeleteWinoAccountSecretsAsync(Guid accountId, CancellationToken cancellationToken = default) => Complete(cancellationToken);

    private static Task Complete(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
