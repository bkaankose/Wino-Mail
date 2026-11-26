using System;
using System.Net;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wino.Authentication;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Helpers;
using Wino.Core.Integration.Processors;
using Wino.Core.PushRelay;
using Wino.Core.Services;
using Wino.Providers.Gmail.Authentication;
using Wino.Providers.Gmail.Google;
using Wino.Providers.Gmail.Integration.Processors;
using Wino.Providers.Gmail.Interfaces;
using Wino.Providers.Gmail.PushRelay;
using Wino.Providers.Gmail.Services;
using Wino.Providers.Gmail.Synchronizers;
using Wino.Providers.Gmail.Synchronizers.Errors.Gmail;
using Wino.Services.Interfaces;

namespace Wino.Providers.Gmail.Providers;

public sealed class GmailProviderModule(
    IMessenger messenger,
    RequestUiChangeCoordinator uiChanges,
    Func<IGmailAuthenticator> authenticator,
    IGmailChangeProcessor changeProcessor,
    IGmailSynchronizerErrorHandlerFactory errorHandlerFactory,
    IMailFilterExecutor mailFilterExecutor,
    IContactService contactService,
    IPictureStorageService contactPictureService,
    ITaskService taskService,
    ICardDavSynchronizationEngine cardDavSynchronizationEngine) : IMailProviderModule
{
    public MailProviderType ProviderType => MailProviderType.Gmail;

    public MailProviderTraits Traits { get; } = new()
    {
        // Gmail's documented send limit, which counts the encoded message.
        MaximumMessageBytes = 25L * 1024 * 1024,
        UsesInteractiveOAuth = true,
        ServesModesFromProvider = true,
        ProviderContactSource = ContactSourceKind.Gmail,
        ProviderTaskSource = TaskSourceKind.Gmail,
        ManagesProviderContactCollections = true,
        ManagesProviderTaskSteps = true,
        AnswersInvitationsThroughProvider = true,
        SupportsContactPhotoWrite = true,
        SupportsAliasSync = true,
        SupportsProfileInfoSync = true,
        StoresMessageInSeveralFolders = true,
        UsesQuotaWindowForTasksAndContacts = true,
        AppliesMailSynchronizationRange = true,
        SupportsMailSynchronizationRangeChange = true,
        TracksMailChangesPerAccount = true,
        LiveCoverage = LiveCoverageKind.RelayMailbox
    };

    public IAuthenticator CreateAuthenticator() => authenticator();

    public IWinoSynchronizerBase CreateSynchronizer(MailAccount account)
        => new GmailSynchronizer(account, messenger, uiChanges, authenticator(), changeProcessor, errorHandlerFactory, mailFilterExecutor,
                                 contactService, contactPictureService, taskService, cardDavSynchronizationEngine);

    public bool CanReuseSynchronizer(IWinoSynchronizerBase existing, MailAccount account) => true;

    public bool IsAuthorizationFailure(Exception exception)
        => exception is GoogleApiException { HttpStatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden };
}

public static class GmailProviderRegistration
{
    public static IServiceCollection AddGmailProvider(this IServiceCollection services)
    {
        services.AddSingleton<IMailProviderModule>(provider => new GmailProviderModule(
            provider.GetRequiredService<IMessenger>(),
            provider.GetRequiredService<RequestUiChangeCoordinator>(),
            () => provider.GetRequiredService<IGmailAuthenticator>(),
            provider.GetRequiredService<IGmailChangeProcessor>(),
            provider.GetRequiredService<IGmailSynchronizerErrorHandlerFactory>(),
            provider.GetRequiredService<IMailFilterExecutor>(),
            provider.GetRequiredService<IContactService>(),
            provider.GetRequiredService<IPictureStorageService>(),
            provider.GetService<ITaskService>(),
            provider.GetService<ICardDavSynchronizationEngine>()));

        services.AddSingleton<IPushRelayMailboxRegistrationBuilder>(provider => new GmailPushRelayMailboxRegistrationBuilder(
            provider.GetRequiredService<IAuthenticationProvider>(),
            provider.GetRequiredService<RelayWellKnownCache>(),
            provider.GetRequiredService<PushRelayAppManagedRegistrationStore>()));

        services.AddTransient<IGmailChangeProcessor, GmailChangeProcessor>();
        services.AddSingleton<IGoogleMailboxProfileService, GoogleMailboxProfileService>();

        // Desktop default; a head that cannot listen on loopback registers its own receiver.
        services.TryAddTransient<IGoogleAuthorizationCodeReceiver>(CreateLoopbackGoogleAuthorizationCodeReceiver);
        services.AddTransient<IGmailAuthenticator>(provider => new GmailAuthenticator(
            provider.GetRequiredService<IAuthenticatorConfig>(),
            provider.GetRequiredService<IGoogleAuthorizationCodeReceiver>(),
            provider.GetRequiredService<IGoogleTokenStore>()));

        services.AddTransient<GmailAuthenticationFailedHandler>();
        services.AddTransient<GmailQuotaExceededHandler>();
        services.AddTransient<GmailRateLimitHandler>();
        services.AddTransient<GmailHistoryExpiredHandler>();
        services.AddTransient<IGmailSynchronizerErrorHandlerFactory, GmailSynchronizerErrorHandlingFactory>();

        return services;
    }

    /// <summary>
    /// Selects the loopback (desktop browser) Google authorization receiver. It replaces any earlier
    /// receiver registration, so a head can state its choice after <see cref="AddGmailProvider"/>.
    /// </summary>
    public static IServiceCollection AddLoopbackGoogleAuthorizationCodeReceiver(this IServiceCollection services)
        => services.Replace(ServiceDescriptor.Transient<IGoogleAuthorizationCodeReceiver>(CreateLoopbackGoogleAuthorizationCodeReceiver));

    public static IGoogleAuthorizationCodeReceiver CreateLoopbackGoogleAuthorizationCodeReceiver(IServiceProvider provider)
        => new WinoGmailCodeReceiver(
            provider.GetRequiredService<IExternalLauncher>(),
            provider.GetService<IExternalBrowserAuthenticationPresenter>(),
            provider.GetRequiredService<IAuthenticatorConfig>().ApplicationDisplayName);
}
