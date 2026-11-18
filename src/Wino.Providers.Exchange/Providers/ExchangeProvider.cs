using System;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Wino.Authentication.Exchange;
using Wino.Authentication.OAuth;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Helpers;
using Wino.Core.Integration.Processors;
using Wino.Core.Services;
using Wino.Core.Synchronizers.Errors.Exchange;
using Wino.Core.Synchronizers.Exchange;
using Wino.Core.Synchronizers.Exchange.ActiveSync;
using Wino.Core.Synchronizers.Mapi;
using Wino.Services;

namespace Wino.Core.Providers;

/// <summary>On-premises Exchange Server, over MAPI over HTTP or Exchange Web Services.</summary>
public sealed class ExchangeProviderModule(
    IMessenger messenger,
    RequestUiChangeCoordinator uiChanges,
    Func<IExchangeAuthenticator> authenticator,
    IExchangeChangeProcessor changeProcessor,
    IExchangeSynchronizerErrorHandlerFactory errorHandlerFactory,
    IContactService contactService,
    IPictureStorageService contactPictureService,
    ITaskService taskService,
    Func<MailAccount, IWinoSynchronizerBase> activeSyncFactory) : IMailProviderModule
{
    public MailProviderType ProviderType => MailProviderType.Exchange;

    // Exchange serves one Contacts folder from this client, so no address books are written back. Its task folders are
    // task lists, but Exchange tasks have no checklist steps.
    public MailProviderTraits Traits { get; } = new()
    {
        ServesModesFromProvider = true,
        ProviderContactSource = ContactSourceKind.Exchange,
        ProviderTaskSource = TaskSourceKind.Exchange,
        ManagesProviderContactCollections = false,
        ManagesProviderTaskSteps = false,
        ArchiveRequiresArchiveFolder = true,
        LiveCoverage = LiveCoverageKind.ExchangeNotifications
    };

    public IAuthenticator CreateAuthenticator() => authenticator();

    public IWinoSynchronizerBase CreateSynchronizer(MailAccount account)
        => ExchangeSynchronizerSelector.Create(
            account,
            messenger,
            uiChanges,
            authenticator() ?? throw new InvalidOperationException("The Exchange authenticator is not registered."),
            changeProcessor ?? throw new InvalidOperationException("The Exchange change processor is not registered."),
            errorHandlerFactory ?? throw new InvalidOperationException("The Exchange error handler factory is not registered."),
            contactService,
            contactPictureService,
            taskService,
            () => activeSyncFactory(account));

    /// <summary>
    /// A synchronizer runs on another transport than the account now asks for when the connection method was changed,
    /// or when a MAPI over HTTP attempt recorded that the server does not offer it. The MAPI synchronizer records that on
    /// its own account object, so comparing the settings alone never sees it.
    /// </summary>
    public bool CanReuseSynchronizer(IWinoSynchronizerBase existing, MailAccount account)
    {
        if (account.ServerInformation == null)
            return true;

        var currentTransport = existing switch
        {
            ActiveSyncExchangeSynchronizer => ExchangeTransport.ActiveSync,
            MapiExchangeSynchronizer => ExchangeTransport.MapiHttp,
            _ => ExchangeTransport.Ews
        };
        return currentTransport == account.ServerInformation.EffectiveExchangeTransport;
    }

    public bool IsAuthorizationFailure(Exception exception) => false;
}

public static class ExchangeProviderRegistration
{
    public static IServiceCollection AddExchangeProvider(this IServiceCollection services)
    {
        services.AddSingleton<IMailProviderModule>(provider => new ExchangeProviderModule(
            provider.GetRequiredService<IMessenger>(),
            provider.GetRequiredService<RequestUiChangeCoordinator>(),
            () => provider.GetRequiredService<IExchangeAuthenticator>(),
            provider.GetService<IExchangeChangeProcessor>(),
            provider.GetService<IExchangeSynchronizerErrorHandlerFactory>(),
            provider.GetRequiredService<IContactService>(),
            provider.GetRequiredService<IPictureStorageService>(),
            provider.GetService<ITaskService>(),
            account => new ActiveSyncExchangeSynchronizer(account,
                provider.GetRequiredService<IMessenger>(),
                provider.GetRequiredService<RequestUiChangeCoordinator>(),
                provider.GetRequiredService<IDatabaseService>(),
                provider.GetRequiredService<IAccountService>(),
                provider.GetRequiredService<IFolderService>(),
                provider.GetRequiredService<IMailService>(),
                provider.GetRequiredService<ICalendarService>(),
                provider.GetRequiredService<IContactService>(),
                provider.GetRequiredService<ITaskService>(),
                provider.GetRequiredService<IMimeFileService>(),
                provider.GetRequiredService<IPictureStorageService>(),
                provider.GetRequiredService<ICalDavChangeProcessor>(),
                provider.GetRequiredService<IApplicationConfiguration>().ApplicationDataFolderPath)));

        services.AddTransient<IExchangeChangeProcessor, ExchangeChangeProcessor>();
        services.AddTransient<IExchangeAutoDiscoveryService, ExchangeAutoDiscoveryService>();
        services.AddTransient<IExchangeAuthCapabilityProbe, ExchangeAuthCapabilityProbe>();
        services.AddTransient<IMapiConnectionProbe, MapiConnectionProbe>();
        services.AddTransient<IExchangeConnectionTester, ExchangeConnectionTester>();

        // One access token cache per process; refresh tokens live in IExchangeTokenStore, never in IOAuthTokenStore.
        services.AddSingleton<ExchangeTokenCache>();
        services.AddTransient<ExchangeNtlmAuthenticator>();
        services.AddTransient<ExchangeOAuthAuthenticator>();
        services.AddTransient<IExchangeAuthenticator, ExchangeAuthenticator>();
        // Interactive Exchange sign-in needs the head's embedded browser; without one it reports itself unsupported
        // and never falls back to the system-browser OAuth flow.
        services.AddTransient<IExchangeInteractiveSignInService>(provider => new ExchangeInteractiveSignInService(
            provider.GetRequiredService<IOAuthProtocolClient>(),
            provider.GetRequiredService<IExchangeTokenStore>(),
            provider.GetRequiredService<ExchangeTokenCache>(),
            () => provider.GetService<IEmbeddedBrowserAuthBroker>()));

        services.AddTransient<ExchangeAuthenticationFailedHandler>();
        services.AddTransient<ExchangeServerBusyHandler>();
        services.AddTransient<ExchangeInvalidServerResponseHandler>();
        services.AddTransient<IExchangeSynchronizerErrorHandlerFactory, ExchangeSynchronizerErrorHandlingFactory>();

        return services;
    }
}
