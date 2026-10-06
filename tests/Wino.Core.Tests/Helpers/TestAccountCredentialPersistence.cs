using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Tests.Helpers;

/// <summary>Explicit legacy representation for existing service tests; never selected by production DI.</summary>
internal sealed class TestAccountCredentialPersistence : IAccountCredentialPersistence
{
    public static readonly TestAccountCredentialPersistence Instance = new();
    public Task<CustomServerInformation> PrepareServerInformationForStorageAsync(CustomServerInformation information, CancellationToken cancellationToken = default) => Task.FromResult(information);
    public Task<WinoAccount> PrepareWinoAccountForStorageAsync(WinoAccount account, CancellationToken cancellationToken = default) => Task.FromResult(account);
    public Task RestoreServerInformationSecretsAsync(CustomServerInformation information, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task RestoreWinoAccountSecretsAsync(WinoAccount account, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task DeleteMailAccountSecretsAsync(Guid accountId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task DeleteWinoAccountSecretsAsync(Guid accountId, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
