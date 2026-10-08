#if DEBUG
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.MacOS.Infrastructure;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>
/// Debug bridge commands: <c>templates</c> opens Settings › Email templates; <c>template-edit [n|new]</c>
/// opens the editor for the n-th stored template (0-based, default 0) or a new one. The list is opened
/// first so the editor has a page to return to after Save or Delete.
/// </summary>
internal static class EmailTemplatesDebug
{
    private static IServiceProvider Services => typeof(MacDebugBridge).GetField("_services", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as IServiceProvider
        ?? throw new InvalidOperationException("no service provider");

    public static void Register()
    {
        MacDebugBridge.Register("templates", _ =>
            Task.FromResult(Services.GetRequiredService<AppKitNavigationService>().Navigate(WinoPage.EmailTemplatesPage) ? "ok" : "refused"));

        MacDebugBridge.Register("template-edit", async args =>
        {
            var navigation = Services.GetRequiredService<AppKitNavigationService>();
            object? parameter = null;
            if (args.Length == 0 || !args[0].Equals("new", StringComparison.OrdinalIgnoreCase))
            {
                var index = args.Length > 0 && int.TryParse(args[0], out var value) ? value : 0;
                var templates = await Services.GetRequiredService<IEmailTemplateService>().GetEmailTemplatesAsync();
                if (index < 0 || index >= templates.Count) return $"no template {index} ({templates.Count} stored)";
                parameter = templates[index].Id;
            }
            if (!navigation.Navigate(WinoPage.EmailTemplatesPage)) return "refused";
            await Task.Delay(400);
            return navigation.Navigate(WinoPage.CreateEmailTemplatePage, parameter) ? "ok" : "refused";
        });
    }
}
#endif
