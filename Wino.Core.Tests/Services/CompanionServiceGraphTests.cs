using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;
using Wino.Core;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Services;
using Wino.Services;

namespace Wino.Core.Tests.Services;

/// <summary>
/// Mirrors the dependency injection container of Wino.BackgroundService and resolves every
/// root the companion process resolves at startup. A missing registration here would
/// otherwise only surface as a startup crash of the deployed companion (e.g. the
/// IAuthenticatorConfig gap that broke launch-on-demand).
/// </summary>
public class CompanionServiceGraphTests
{
    private static ServiceProvider BuildCompanionLikeProvider()
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.RegisterCoreServices();
        services.RegisterCompanionServices();

        // Companion-specific overrides, mirroring Wino.BackgroundService.Program.
        // Windows-only implementations are replaced with test doubles.
        services.AddSingleton<IAuthenticatorConfig, MailAuthenticatorConfiguration>();
        services.AddSingleton(Mock.Of<INativeAppService>());
        services.AddSingleton(Mock.Of<IAppMetadataService>());
        services.AddTransient(_ => Mock.Of<IConfigurationService>());
        services.AddSingleton<IPreferencesService, PreferencesService>();
        services.AddTransient(_ => Mock.Of<INotificationBuilder>());
        services.AddTransient(_ => Mock.Of<IKeyPressService>());
        services.AddSingleton(Mock.Of<IStoreManagementService>());
        services.AddSingleton(Mock.Of<IMailDialogService>());

        var provider = services.BuildServiceProvider();

        // The companion sets these before resolving anything that touches the file system.
        var configuration = provider.GetRequiredService<IApplicationConfiguration>();
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"wino-di-tests-{Guid.NewGuid():N}");
        configuration.ApplicationDataFolderPath = Directory.CreateDirectory(Path.Combine(temporaryRoot, "local")).FullName;
        configuration.PublisherSharedFolderPath = Directory.CreateDirectory(Path.Combine(temporaryRoot, "shared")).FullName;
        configuration.ApplicationTempFolderPath = Directory.CreateDirectory(Path.Combine(temporaryRoot, "temp")).FullName;

        return provider;
    }

    public static TheoryData<Type> CompanionRootServices => new()
    {
        // Initialization roots.
        typeof(IDatabaseService),
        typeof(ITranslationService),
        typeof(SynchronizationManagerInitializer),
        typeof(IWinoLogger),
        typeof(IApplicationConfiguration),

        // Services the SynchronizationManager initializer resolves.
        typeof(ISynchronizerFactory),
        typeof(IImapTestService),
        typeof(IAuthenticationProvider),
        typeof(IWinoTelemetryService),

        // Every interface behind the generated WinoRpcDispatcher.
        typeof(IAccountService),
        typeof(ICalendarService),
        typeof(IContactPictureFileService),
        typeof(IContactService),
        typeof(IEmailTemplateService),
        typeof(IFolderService),
        typeof(IKeyboardShortcutService),
        typeof(IMailCategoryService),
        typeof(IMailService),
        typeof(ISentMailReceiptService),
        typeof(ISignatureService),
        typeof(ISmimeService),
        typeof(ISynchronizationManager),
        typeof(IThumbnailCacheService),
        typeof(IWinoAccountDataSyncService),
        typeof(IWinoAccountProfileService),
        typeof(IWinoRequestDelegator),

        // Internals used by the request pipeline and loops.
        typeof(IWinoRequestProcessor),
        typeof(IMimeFileService),
        typeof(IMimeStorageService),
        typeof(ICalendarIcsFileService),
    };

    [Theory]
    [MemberData(nameof(CompanionRootServices))]
    public void CompanionRootService_CanBeResolved(Type serviceType)
    {
        using var provider = BuildCompanionLikeProvider();

        var resolved = provider.GetRequiredService(serviceType);

        Assert.NotNull(resolved);
    }
}
