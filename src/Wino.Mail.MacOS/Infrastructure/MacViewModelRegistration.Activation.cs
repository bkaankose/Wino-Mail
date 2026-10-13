using Microsoft.Extensions.DependencyInjection;
using Wino.Mail.ViewModels.Activation;
using Wino.Mail.ViewModels.Lifecycle;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>OS activation services (URL schemes, opened files, launch at login) and the startup order. Owned by the activation work (WS8).</summary>
public static partial class MacViewModelRegistration
{
    static partial void RegisterActivationServices(IServiceCollection services)
    {
        // Launch at login (MacStartupIntegrationService) is registered with the platform services in Composition.
        services.AddSingleton<IApplicationShellHost, AppKitApplicationShellHost>();
        services.AddSingleton<ActivationRouter>();
        services.AddSingleton<MacActivationCoordinator>();
        services.AddSingleton<ApplicationLifecycleCoordinator>();
    }
}
