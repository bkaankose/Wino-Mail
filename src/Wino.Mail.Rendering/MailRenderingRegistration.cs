using Microsoft.Extensions.DependencyInjection;
using Wino.Mail.ViewModels;
using Wino.Services.Interfaces;

namespace Wino.Mail.Rendering;

public static class MailRenderingRegistration
{
    /// <summary>
    /// The engine host and the services that read through it. The head registers its <see cref="IMailEngineBackendFactory"/>.
    /// </summary>
    public static IServiceCollection AddMailRendering(this IServiceCollection services)
    {
        services.AddSingleton<MailEngineHost>();
        services.AddSingleton<IMailUnsubscribeReader, EngineUnsubscribeReader>();
        services.AddSingleton<ISpellCheckLanguageSource, EngineSpellCheckLanguageSource>();
        services.AddSingleton<IMailFontSource, EngineMailFontSource>();
        return services;
    }
}
