using Microsoft.Extensions.DependencyInjection;

namespace Wino.Mail.Linux.Infrastructure;

internal static partial class LinuxViewModelRegistration
{
    static partial void RegisterMail(IServiceCollection services)
    {
        services.AddSingleton<LinuxSenderPictureProvider>();
    }
}
