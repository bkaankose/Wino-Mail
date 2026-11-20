using System;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Helpers;
using Wino.Core.Integration;
using Wino.Core.Integration.Processors;
using Wino.Core.Services;
using Wino.Core.Interfaces;
using Wino.Providers.Imap.Integration;
using Wino.Providers.Imap.Integration.Processors;
using Wino.Providers.Imap.Interfaces;
using Wino.Providers.Imap.Services;
using Wino.Providers.Imap.Synchronizers;
using Wino.Providers.Imap.Synchronizers.Errors.Imap;
using Wino.Providers.Imap.Synchronizers.ImapSync;
using Wino.Services.Interfaces;

namespace Wino.Providers.Imap.Providers;

public sealed class ImapProviderModule(
    IMessenger messenger,
    RequestUiChangeCoordinator uiChanges,
    IImapChangeProcessor changeProcessor,
    IApplicationConfiguration applicationConfiguration,
    UnifiedImapSynchronizer unifiedImapSynchronizer,
    IImapSynchronizerErrorHandlerFactory errorHandlerFactory,
    ICalDavSynchronizationEngine calDavSynchronizationEngine,
    ICalendarService calendarService,
    IMailFilterExecutor mailFilterExecutor,
    IServerCertificateTrustService serverCertificateTrustService,
    ICardDavSynchronizationEngine cardDavSynchronizationEngine,
    IContactService contactService,
    ITaskService taskService,
    ISmtpTransport smtpTransport,
    IOAuthTokenService oauthTokenService,
    ICalDavTaskSynchronizationEngine calDavTaskSynchronizationEngine) : IMailProviderModule
{
    public MailProviderType ProviderType => MailProviderType.IMAP4;

    public MailProviderTraits Traits { get; } = new()
    {
        ManagesProviderContactCollections = true,
        ManagesProviderTaskSteps = true,
        SupportsDavIntegration = true,
        ArchiveRequiresArchiveFolder = true,
        MovesJunkAsFolderMove = true,
        LiveCoverage = LiveCoverageKind.ImapIdle
    };

    public IAuthenticator CreateAuthenticator() => null;

    public IWinoSynchronizerBase CreateSynchronizer(MailAccount account)
        => new ImapSynchronizer(
            account,
            messenger,
            uiChanges,
            changeProcessor,
            applicationConfiguration,
            unifiedImapSynchronizer,
            errorHandlerFactory,
            calDavSynchronizationEngine,
            calendarService,
            mailFilterExecutor,
            serverCertificateTrustService,
            cardDavSynchronizationEngine,
            contactService,
            taskService,
            smtpTransport,
            oauthTokenService,
            calDavTaskSynchronizationEngine);

    public bool CanReuseSynchronizer(IWinoSynchronizerBase existing, MailAccount account) => true;

    public bool IsAuthorizationFailure(Exception exception) => false;
}

public sealed class Pop3ProviderModule(
    IMessenger messenger,
    RequestUiChangeCoordinator uiChanges,
    IPop3ClientFactory clientFactory,
    IPop3PersistenceService persistenceService,
    IMailService mailService,
    IFolderService folderService,
    IAccountService accountService,
    ISmtpTransport smtpTransport,
    IMimeFileService mimeFileService,
    IMailFilterExecutor mailFilterExecutor) : IMailProviderModule
{
    public MailProviderType ProviderType => MailProviderType.POP3;

    public MailProviderTraits Traits { get; } = new()
    {
        ManagesProviderContactCollections = true,
        ManagesProviderTaskSteps = true,
        ArchiveRequiresArchiveFolder = true
    };

    public IAuthenticator CreateAuthenticator() => null;

    public IWinoSynchronizerBase CreateSynchronizer(MailAccount account)
        => new Pop3Synchronizer(
            account,
            messenger,
            uiChanges,
            clientFactory ?? throw new InvalidOperationException("POP3 client factory is not registered."),
            persistenceService ?? throw new InvalidOperationException("POP3 persistence service is not registered."),
            mailService ?? throw new InvalidOperationException("Mail service is not registered."),
            folderService ?? throw new InvalidOperationException("Folder service is not registered."),
            accountService,
            smtpTransport ?? throw new InvalidOperationException("SMTP transport is not registered."),
            mimeFileService ?? throw new InvalidOperationException("MIME file service is not registered."),
            mailFilterExecutor);

    public bool CanReuseSynchronizer(IWinoSynchronizerBase existing, MailAccount account) => true;

    public bool IsAuthorizationFailure(Exception exception) => false;
}

/// <summary>IMAP, POP3 and SMTP: the providers that sign in with the server settings stored on the account.</summary>
public static class ImapProviderRegistration
{
    public static IServiceCollection AddImapProvider(this IServiceCollection services)
    {
        services.AddSingleton<IMailProviderModule>(provider => new ImapProviderModule(
            provider.GetRequiredService<IMessenger>(),
            provider.GetRequiredService<RequestUiChangeCoordinator>(),
            provider.GetRequiredService<IImapChangeProcessor>(),
            provider.GetRequiredService<IApplicationConfiguration>(),
            provider.GetRequiredService<UnifiedImapSynchronizer>(),
            provider.GetRequiredService<IImapSynchronizerErrorHandlerFactory>(),
            provider.GetRequiredService<ICalDavSynchronizationEngine>(),
            provider.GetRequiredService<ICalendarService>(),
            provider.GetRequiredService<IMailFilterExecutor>(),
            provider.GetRequiredService<IServerCertificateTrustService>(),
            provider.GetService<ICardDavSynchronizationEngine>(),
            provider.GetRequiredService<IContactService>(),
            provider.GetService<ITaskService>(),
            provider.GetService<ISmtpTransport>(),
            provider.GetService<IOAuthTokenService>(),
            provider.GetService<ICalDavTaskSynchronizationEngine>()));

        services.AddSingleton<IMailProviderModule>(provider => new Pop3ProviderModule(
            provider.GetRequiredService<IMessenger>(),
            provider.GetRequiredService<RequestUiChangeCoordinator>(),
            provider.GetService<IPop3ClientFactory>(),
            provider.GetService<IPop3PersistenceService>(),
            provider.GetService<IMailService>(),
            provider.GetService<IFolderService>(),
            provider.GetRequiredService<IAccountService>(),
            provider.GetService<ISmtpTransport>(),
            provider.GetService<IMimeFileService>(),
            provider.GetRequiredService<IMailFilterExecutor>()));

        services.AddTransient<IImapChangeProcessor, ImapChangeProcessor>();
        services.AddTransient<UnifiedImapSynchronizer>();
        services.AddTransient<IMailServerTestService, MailServerTestService>();
        services.AddTransient<IImapAuthCapabilityProbe, ImapAuthCapabilityProbe>();
        services.AddTransient<IImapSetupResolver, ImapSetupResolver>();
        services.AddTransient<IPop3ClientFactory, MailKitPop3ClientFactory>();
        services.AddTransient<ISmtpTransport, MailKitSmtpTransport>();

        services.AddTransient<ImapConnectionLostHandler>();
        services.AddTransient<ImapAuthenticationFailedHandler>();
        services.AddTransient<ImapFolderNotFoundHandler>();
        services.AddTransient<ImapProtocolErrorHandler>();
        services.AddTransient<IImapSynchronizerErrorHandlerFactory, ImapSynchronizerErrorHandlingFactory>();

        return services;
    }
}
