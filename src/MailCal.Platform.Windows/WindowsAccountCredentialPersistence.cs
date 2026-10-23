using System;
using System.Threading;
using System.Threading.Tasks;
using MailCal.Core.Domain.Entities.Shared;
using MailCal.Core.Domain.Interfaces;

namespace MailCal.Platform.Windows;

/// <summary>Preserves the existing Windows SQLite representation without a credential migration.</summary>
public sealed class WindowsAccountCredentialPersistence : IAccountCredentialPersistence
{
    public Task<CustomServerInformation> PrepareServerInformationForStorageAsync(CustomServerInformation information, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(information);
    }

    public Task RestoreServerInformationSecretsAsync(CustomServerInformation information, CancellationToken cancellationToken = default) => Complete(cancellationToken);
    public Task DeleteMailAccountSecretsAsync(Guid accountId, CancellationToken cancellationToken = default) => Complete(cancellationToken);

    private static Task Complete(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
