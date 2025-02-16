using Microsoft.Extensions.DependencyInjection;
using Wino.Core;

namespace Wino.Mail.ViewModels;

public static class MailViewModelsContainerSetup
{
    public static void RegisterViewModelService(this IServiceCollection services)
    {
        // View models use core services.
        services.RegisterCoreServices();
    }
}
