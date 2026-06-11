using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Interfaces;

namespace Wino.Services;

public static class SharedServicesContainerSetup
{
    /// <summary>
    /// Dependency-light services used in-process by both the UI and the companion.
    /// Database/MimeKit/Ical.Net-backed services are companion-only and registered by
    /// Wino.Services.ServicesContainerSetup.RegisterCompanionServices; the UI reaches them
    /// through the generated RPC proxies instead.
    /// </summary>
    public static void RegisterSharedServices(this IServiceCollection services)
    {
        services.AddSingleton<ITranslationService, TranslationService>();
        services.AddSingleton<IApplicationConfiguration, ApplicationConfiguration>();
        services.AddSingleton<IWinoLogger, WinoLogger>();
        services.AddSingleton<IWinoTelemetryService, WinoTelemetryService>();
        services.AddSingleton<ILaunchProtocolService, LaunchProtocolService>();
        services.AddSingleton<IShareActivationService, ShareActivationService>();
        services.AddSingleton<IMimeFileService, MimeFileService>();
        services.AddSingleton<ICalendarIcsFileService, CalendarIcsFileService>();
        services.AddTransient<IMimeStorageService, MimeStorageService>();

        services.AddTransient<IContextMenuItemService, ContextMenuItemService>();
        services.AddTransient<ICalendarContextMenuItemService, CalendarContextMenuItemService>();
        services.AddTransient<ISpecialImapProviderConfigResolver, SpecialImapProviderConfigResolver>();
        services.AddSingleton<IUpdateManager, UpdateManager>();
        services.AddTransient<IFontService, FontService>();
        services.AddTransient<IAutoDiscoveryService, AutoDiscoveryService>();
        services.AddTransient<IUnsubscriptionService, UnsubscriptionService>();
    }
}
