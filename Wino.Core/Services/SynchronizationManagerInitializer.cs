using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Services;

namespace Wino.Core.Services;

/// <summary>
/// Service responsible for initializing the SynchronizationManager during app startup.
/// </summary>
public class SynchronizationManagerInitializer : IInitializeAsync
{
    private readonly IServiceProvider _serviceProvider;

    public SynchronizationManagerInitializer(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public async Task InitializeAsync()
    {
        var synchronizerFactory = _serviceProvider.GetRequiredService<ISynchronizerFactory>();
        var imapTestService = _serviceProvider.GetRequiredService<IImapTestService>();
        var accountService = _serviceProvider.GetRequiredService<IAccountService>();
        var authenticationProvider = _serviceProvider.GetRequiredService<IAuthenticationProvider>();

        // Cast to concrete type to access CreateNewSynchronizer method
        var concreteSynchronizerFactory = synchronizerFactory as SynchronizerFactory;
        
        await SynchronizationManager.Instance.InitializeAsync(concreteSynchronizerFactory, imapTestService, accountService, authenticationProvider);
    }
}