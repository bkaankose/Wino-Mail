#if DEBUG
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// <c>debug-compose-signature</c> (<c>as</c>) on the active composer: <c>['list']</c> (signatures and the selected one),
/// <c>['apply', name|'none']</c> (the menu's path), <c>['menu']</c> opens the signature menu, <c>['menu-shot', png]</c>,
/// <c>['menu-click', text]</c>, <c>['templates']</c> opens the template popover, <c>['template', name]</c> picks one in it
/// (opening it first), <c>['filter', text]</c>, <c>['popover-shot', png]</c>, <c>['popover-click', text]</c> (Manage/Create),
/// <c>['seed-template', name, subject, htmlFile]</c>, <c>['clear-templates']</c> (names starting with "Lab ").
/// Signatures are seeded with <c>debug-signatures seed</c>.
/// </summary>
internal static class ComposeSignatureDebugActions
{
    public static void Register(Adw.Application application, Func<IServiceProvider?> services)
    {
        var action = Gio.SimpleAction.New("debug-compose-signature", GLib.VariantType.New("as"));
        action.OnActivate += (sender, args) => _ = RunAsync(services, args.Parameter?.GetStrv(out _) ?? []);
        application.AddAction(action);
    }

    private static async Task RunAsync(Func<IServiceProvider?> services, string[] arguments)
    {
        try
        {
            var command = Argument(arguments, 0);
            var provider = services() ?? throw new InvalidOperationException("The services are not ready.");
            var templates = provider.GetRequiredService<IEmailTemplateService>();
            switch (command)
            {
                case "seed-template":
                {
                    var created = await templates.CreateEmailTemplateAsync(new EmailTemplate
                    {
                        Id = Guid.NewGuid(),
                        Name = Argument(arguments, 1),
                        Subject = Argument(arguments, 2),
                        HtmlContent = await File.ReadAllTextAsync(Argument(arguments, 3)),
                    });
                    Log($"template {created.Name} created ({created.Id})");
                    return;
                }
                case "clear-templates":
                {
                    var removed = 0;
                    foreach (var template in await templates.GetEmailTemplatesAsync())
                    {
                        if (template.Name?.StartsWith("Lab ", StringComparison.Ordinal) != true) continue;
                        await templates.DeleteEmailTemplateAsync(template);
                        removed++;
                    }
                    Log($"{removed} lab templates deleted");
                    return;
                }
            }

            var view = ComposeDebugActions.Current ?? throw new InvalidOperationException("No composer is shown.");
            var vm = view.ViewModel;
            switch (command)
            {
                case "list":
                    Log($"account={vm.ComposingAccount?.Address} selected={vm.SelectedSignature?.Name ?? "none"} signatures={string.Join("|", vm.AvailableSignatures.Select(static s => s.Name))} " +
                        $"menu={view.SignatureMenu?.AccessibleName} sensitive={view.SignatureMenu?.Root.GetSensitive()}");
                    break;
                case "apply":
                {
                    var name = Argument(arguments, 1);
                    AccountSignature? signature = name == "none" ? null
                        : vm.AvailableSignatures.FirstOrDefault(s => s.Name == name) ?? throw new InvalidOperationException($"No signature '{name}'.");
                    await vm.ApplySignatureCommand.ExecuteAsync(signature);
                    break;
                }
                case "menu": (view.SignatureMenu ?? throw new InvalidOperationException("No signature menu.")).Open(); break;
                case "menu-shot": PopoverDebug.Capture(OpenMenu(view), Argument(arguments, 1)); break;
                case "menu-click": PopoverDebug.Click(OpenMenu(view), Argument(arguments, 1)); break;
                case "templates": await view.ShowTemplatesAsync(); break;
                case "template":
                    if (view.TemplatePopover is null) await view.ShowTemplatesAsync();
                    (view.TemplatePopover ?? throw new InvalidOperationException("No template popover.")).Pick(Argument(arguments, 1));
                    break;
                case "filter": Popover(view).Filter(Argument(arguments, 1)); Log($"visible={Popover(view).VisibleCount}"); break;
                case "popover-shot": PopoverDebug.Capture(Popover(view).Popover, Argument(arguments, 1)); break;
                case "popover-click":
                {
                    var text = Argument(arguments, 1);
                    var button = FindButton(Popover(view).Popover, text) ?? throw new InvalidOperationException($"No button '{text}'.");
                    button.Activate();
                    break;
                }
                default: throw new ArgumentException($"'{command}' is not a debug-compose-signature command.");
            }
            Log("done " + string.Join(' ', arguments));
        }
        catch (Exception exception) { Serilog.Log.Error(exception, "Debug action debug-compose-signature failed."); }
    }

    private static Wino.Presentation.Linux.WinoMenuPopover OpenMenu(Views.Mail.Compose.ComposeView view)
        => view.SignatureMenu?.OpenMenu ?? throw new InvalidOperationException("The signature menu is not open.");

    private static Views.Mail.Compose.ComposeTemplatePopover Popover(Views.Mail.Compose.ComposeView view)
        => view.TemplatePopover ?? throw new InvalidOperationException("The template popover is not open.");

    private static Gtk.Button? FindButton(Gtk.Widget widget, string text)
    {
        if (widget is Gtk.Button button && button.GetLabel() == text) return button;
        for (var child = widget.GetFirstChild(); child is not null; child = child.GetNextSibling())
        {
            if (FindButton(child, text) is { } found) return found;
        }
        return null;
    }

    private static void Log(string line) => Serilog.Log.Information("compose-signature-debug: {Line}", line);

    private static string Argument(string[] arguments, int index)
        => index < arguments.Length ? arguments[index] : throw new ArgumentException($"Argument {index} is missing.");
}
#endif
