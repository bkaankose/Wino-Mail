#if DEBUG
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Enums;
using Wino.Mail.UIKit.Infrastructure;

namespace Wino.Mail.UIKit.Views.Mail;

/// <summary>
/// Debug-only demo account for the Simulator (bridge command <c>seedmail</c>, both idioms): the shared
/// <see cref="Wino.Mail.ViewModels.Diagnostics.MailDebugSeed"/>, then Mail opens.
/// </summary>
internal static class MailDebugSeed
{
    public static async Task<string> SeedAsync(IServiceProvider services)
    {
        var result = await Wino.Mail.ViewModels.Diagnostics.MailDebugSeed.SeedAsync(services);
        services.GetRequiredService<UIKitNavigationService>().ChangeApplicationMode(WinoApplicationMode.Mail);
        return result;
    }
}
#endif
