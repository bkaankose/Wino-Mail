using Microsoft.Extensions.DependencyInjection;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>OS activation services (URL schemes, opened files, launch at login). Owned by the activation work (WS8).</summary>
public static partial class MacViewModelRegistration
{
    static partial void RegisterActivationServices(IServiceCollection services)
    {
        // Launch at login (MacStartupIntegrationService) is registered with the platform services in Composition.
        services.AddSingleton<MacActivationCoordinator>();
    }
}
