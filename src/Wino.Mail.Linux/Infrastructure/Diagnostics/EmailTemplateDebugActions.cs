#if DEBUG
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Linux.Views.Settings;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// <c>debug-templates</c> (<c>as</c>): <c>['open']</c> (Settings › Email templates), <c>['list', path]</c> (id | name | subject |
/// body length | preview, read fresh from the service), <c>['new']</c> and <c>['edit', name]</c> (the list page's commands),
/// and on the open editor page <c>['set-body', html]</c>, <c>['get-body']</c>, <c>['save']</c>, <c>['ctrl-s']</c> (the page's Ctrl+S handler), <c>['state']</c>. Templates are seeded
/// and cleared with <c>debug-compose-signature seed-template|clear-templates</c>.
/// </summary>
internal static class EmailTemplateDebugActions
{
    public static void Register(Adw.Application application, Func<IServiceProvider?> services)
    {
        var action = Gio.SimpleAction.New("debug-templates", GLib.VariantType.New("as"));
        action.OnActivate += (sender, args) => _ = RunAsync(services, args.Parameter?.GetStrv(out _) ?? []);
        application.AddAction(action);
    }

    private static async Task RunAsync(Func<IServiceProvider?> services, string[] arguments)
    {
        try
        {
            var provider = services() ?? throw new InvalidOperationException("The services are not ready.");
            var command = Argument(arguments, 0);
            switch (command)
            {
                case "open":
                    await provider.GetRequiredService<ISettingsWindowPresenter>().ShowAsync(WinoPage.EmailTemplatesPage, null);
                    break;
                case "list":
                {
                    var text = new StringBuilder();
                    foreach (var template in await provider.GetRequiredService<IEmailTemplateService>().GetEmailTemplatesAsync())
                        text.AppendLine($"{template.Id} | {template.Name} | {template.Subject} | {template.HtmlContent?.Length ?? 0} | {HtmlPreviewText.From(template.HtmlContent)}");
                    DebugActions.WriteText(Argument(arguments, 1), text.ToString());
                    break;
                }
                case "new": List().ViewModel.CreateTemplate(); break;
                case "edit":
                {
                    var name = Argument(arguments, 1);
                    var template = List().ViewModel.EmailTemplates.FirstOrDefault(t => t.Name == name) ?? throw new InvalidOperationException($"No template '{name}'.");
                    List().ViewModel.OpenTemplate(template);
                    break;
                }
                case "set-body": await (Editor().DebugEditor ?? throw new InvalidOperationException("No editor.")).SetHtmlAsync(Argument(arguments, 1)); break;
                case "get-body": Log("body " + await (Editor().DebugEditor ?? throw new InvalidOperationException("No editor.")).GetHtmlBodyAsync()); return;
                case "save": await Editor().SaveAsync(); break;
                case "ctrl-s": Serilog.Log.Information("Debug action: templates ctrl-s handled={Handled}", Editor().HandleKey((uint)Gdk.Constants.KEY_s, Gdk.ModifierType.ControlMask)); break;
                case "state":
                {
                    var page = Editor();
                    var vm = page.ViewModel;
                    Log($"state title={page.PageTitle} existing={vm.IsExistingTemplate} name={vm.TemplateName} subject={vm.TemplateSubject} ready={page.DebugEditor?.IsReady}");
                    return;
                }
                default: throw new ArgumentException($"'{command}' is not a debug-templates command.");
            }
            Log("done " + string.Join(' ', arguments));
        }
        catch (Exception exception) { Serilog.Log.Error(exception, "Debug action debug-templates failed."); }
    }

    private static EmailTemplatesPage List() => EmailTemplatesPage.DebugCurrent ?? throw new InvalidOperationException("The templates page is not shown.");

    private static CreateEmailTemplatePage Editor() => CreateEmailTemplatePage.DebugCurrent ?? throw new InvalidOperationException("The template editor is not shown.");

    private static void Log(string line) => Serilog.Log.Information("templates-debug: {Line}", line);

    private static string Argument(string[] arguments, int index)
        => index < arguments.Length ? arguments[index] : throw new ArgumentException($"Argument {index} is missing.");
}
#endif
