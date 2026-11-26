using System;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Wino.Authentication.OAuth;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Helpers;
using Wino.Core.Integration.Processors;
using Wino.Core.Services;
using Wino.Providers.Exchange.Authentication.Exchange;
using Wino.Providers.Exchange.Integration.Processors;
using Wino.Providers.Exchange.Interfaces;
using Wino.Providers.Exchange.Services;
using Wino.Providers.Exchange.Synchronizers.Errors.Exchange;
using Wino.Providers.Exchange.Synchronizers.Exchange;
using Wino.Providers.Exchange.Synchronizers.Mapi;
using Wino.Services;
using Wino.Services.Interfaces;
using Wino.Providers.Exchange.Synchronizers.Exchange.ActiveSync;

namespace Wino.Providers.Exchange.Providers;

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
    IMailCategoryService mailCategoryService,
    Func<MailAccount, IWinoSynchronizerBase> activeSyncFactory) : IMailProviderModule
{
    public MailProviderType ProviderType => MailProviderType.Exchange;

    // Exchange address books are the mailbox's contact folders and are not written back. Its task folders are task lists,
    // but Exchange tasks have no checklist steps.
    public MailProviderTraits Traits { get; } = new()
    {
        ServesModesFromProvider = true,
        ProviderContactSource = ContactSourceKind.Exchange,
        ProviderTaskSource = TaskSourceKind.Exchange,
        ManagesProviderContactCollections = false,
        ManagesProviderTaskSteps = false,
        ArchiveRequiresArchiveFolder = true,
        SupportsProfileInfoSync = true,
        SupportsContactPhotoWrite = true,
        LiveCoverage = LiveCoverageKind.ExchangeNotifications
    };

    // ActiveSync reads the mailbox's addresses from Settings UserInformation. It carries a message's categories by name but
    // has no command for the category list, so assignments synchronize and the list does not. It has no rules command.
    private MailProviderTraits ActiveSyncTraits => field ??= Traits with
    {
        SupportsAliasSync = true,
        SynchronizesCategoryAssignments = true,
        CategorySource = MailCategorySource.Exchange
    };

    // EWS and MAPI read and write the mailbox's category list (MS-OXOCFG) and each mail's categories, and reach the Inbox
    // rules (MS-OXWSRULES, MS-OXORULE).
    private MailProviderTraits EwsTraits => field ??= Traits with
    {
        SupportsCategorySync = true,
        SynchronizesCategoryAssignments = true,
        CategorySource = MailCategorySource.Exchange,
        SupportsProviderMailFilters = true
    };

    // MAPI reads the mailbox's addresses from its address book entry; EWS has no operation that returns all of them.
    private MailProviderTraits MapiTraits => field ??= EwsTraits with { SupportsAliasSync = true };

    // An invitation in the mail is answered through the stored event, which exists only when the calendar comes from
    // the server. On a local calendar the answer is a reply sent from the mail instead.
    private MailProviderTraits ActiveSyncProviderCalendarTraits => field ??= ActiveSyncTraits with { AnswersInvitationsThroughProvider = true };
    private MailProviderTraits EwsProviderCalendarTraits => field ??= EwsTraits with { AnswersInvitationsThroughProvider = true };
    private MailProviderTraits MapiProviderCalendarTraits => field ??= MapiTraits with { AnswersInvitationsThroughProvider = true };

    public MailProviderTraits GetTraits(MailAccount account)
    {
        var isCalendarFromServer = account.IsCalendarAccessGranted
            && account.GetEffectiveCalendarIntegrationSource() == AccountIntegrationSource.Provider;

        return (account.ServerInformation?.EffectiveExchangeTransport, isCalendarFromServer) switch
        {
            (ExchangeTransport.ActiveSync, true) => ActiveSyncProviderCalendarTraits,
            (ExchangeTransport.ActiveSync, false) => ActiveSyncTraits,
            (ExchangeTransport.MapiHttp, true) => MapiProviderCalendarTraits,
            (ExchangeTransport.MapiHttp, false) => MapiTraits,
            (_, true) => EwsProviderCalendarTraits,
            _ => EwsTraits
        };
    }

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
            mailCategoryService,
            () => activeSyncFactory(account));

    // The transport is fixed when the account is set up, so a synchronizer never runs on another one.
    public bool CanReuseSynchronizer(IWinoSynchronizerBase existing, MailAccount account) => true;

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
            provider.GetRequiredService<IMailCategoryService>(),
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
                provider.GetRequiredService<IApplicationConfiguration>().ApplicationDataFolderPath,
                provider.GetRequiredService<IExchangeSynchronizerErrorHandlerFactory>(),
                provider.GetRequiredService<IMailCategoryService>())));

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
