using System;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Graph.Models.ODataErrors;
using Microsoft.Identity.Client;
using Microsoft.Kiota.Abstractions;
using Wino.Authentication;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Helpers;
using Wino.Core.Integration.Processors;
using Wino.Core.PushRelay;
using Wino.Core.Services;
using Wino.Providers.Outlook.Authentication;
using Wino.Providers.Outlook.Integration.Processors;
using Wino.Providers.Outlook.Interfaces;
using Wino.Providers.Outlook.PushRelay;
using Wino.Providers.Outlook.Services;
using Wino.Providers.Outlook.Synchronizers;
using Wino.Providers.Outlook.Synchronizers.Errors.Outlook;
using Wino.Services.Interfaces;

namespace Wino.Providers.Outlook.Providers;

public sealed class OutlookProviderModule(
    IMessenger messenger,
    RequestUiChangeCoordinator uiChanges,
    Func<IOutlookAuthenticator> authenticator,
    IOutlookChangeProcessor changeProcessor,
    IOutlookSynchronizerErrorHandlerFactory errorHandlerFactory,
    IMailCategoryService mailCategoryService,
    IMailFilterExecutor mailFilterExecutor,
    IContactService contactService,
    IPictureStorageService contactPictureService,
    ITaskService taskService,
    ICardDavSynchronizationEngine cardDavSynchronizationEngine) : IMailProviderModule
{
    public MailProviderType ProviderType => MailProviderType.Outlook;

    public MailProviderTraits Traits { get; } = new()
    {
        UsesInteractiveOAuth = true,
        ServesModesFromProvider = true,
        ProviderContactSource = ContactSourceKind.Outlook,
        ProviderTaskSource = TaskSourceKind.Outlook,
        ManagesProviderContactCollections = true,
        ManagesProviderTaskSteps = true,
        AnswersInvitationsThroughProvider = true,
        ArchiveRequiresArchiveFolder = true,
        SupportsCategorySync = true,
        SupportsAliasSync = true,
        SupportsProfileInfoSync = true,
        SupportsFocusedInbox = true,
        SupportsProviderMailFilters = true,
        RemovesDeclinedEvents = true,
        AppliesMailSynchronizationRange = true,
        SupportsMailSynchronizationRangeChange = true,
        LiveCoverage = LiveCoverageKind.RelayMailbox
    };

    public IAuthenticator CreateAuthenticator() => authenticator();

    public IWinoSynchronizerBase CreateSynchronizer(MailAccount account)
        => new OutlookSynchronizer(account, messenger, uiChanges, authenticator(), changeProcessor, errorHandlerFactory, mailCategoryService,
                                   mailFilterExecutor, contactService, contactPictureService, taskService, cardDavSynchronizationEngine);

    public bool CanReuseSynchronizer(IWinoSynchronizerBase existing, MailAccount account) => true;

    public bool IsAuthorizationFailure(Exception exception)
        => exception is ODataError { ResponseStatusCode: 401 or 403 }
            or ApiException { ResponseStatusCode: 401 or 403 }
            or MsalUiRequiredException;
}

public static class OutlookProviderRegistration
{
    public static IServiceCollection AddOutlookProvider(this IServiceCollection services)
    {
        services.AddSingleton<IMailProviderModule>(provider => new OutlookProviderModule(
            provider.GetRequiredService<IMessenger>(),
            provider.GetRequiredService<RequestUiChangeCoordinator>(),
            () => provider.GetRequiredService<IOutlookAuthenticator>(),
            provider.GetRequiredService<IOutlookChangeProcessor>(),
            provider.GetRequiredService<IOutlookSynchronizerErrorHandlerFactory>(),
            provider.GetRequiredService<IMailCategoryService>(),
            provider.GetRequiredService<IMailFilterExecutor>(),
            provider.GetRequiredService<IContactService>(),
            provider.GetRequiredService<IPictureStorageService>(),
            provider.GetService<ITaskService>(),
            provider.GetService<ICardDavSynchronizationEngine>()));

        services.AddSingleton<IPushRelayMailboxRegistrationBuilder>(provider => new GraphPushRelayMailboxRegistrationBuilder(
            provider.GetRequiredService<IAuthenticationProvider>(),
            provider.GetRequiredService<RelayWellKnownCache>(),
            provider.GetRequiredService<PushRelayAppManagedRegistrationStore>()));

        services.AddTransient<IOutlookChangeProcessor, OutlookChangeProcessor>();
        services.AddSingleton<IMicrosoftUserRealmService, MicrosoftUserRealmService>();
        services.AddSingleton<OutlookAccessTokenCache>();
        services.AddTransient<IOutlookAuthenticator, OutlookAuthenticator>();

        services.AddTransient<ObjectCannotBeDeletedHandler>();
        services.AddTransient<DeltaTokenExpiredHandler>();
        services.AddTransient<OutlookRateLimitHandler>();
        services.AddTransient<OutlookAuthenticationFailedHandler>();
        services.AddTransient<IOutlookSynchronizerErrorHandlerFactory, OutlookSynchronizerErrorHandlingFactory>();

        return services;
    }
}
