#if DEBUG
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Linux.Views.Dialogs;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// <c>debug-rich-editor</c> (<c>as</c>): <c>['open'(, 'tabbed')]</c> shows a <see cref="WinoRichTextEditorView"/> in an
/// <c>Adw.Dialog</c> titled "Rich editor", <c>['set', html]</c>, <c>['get']</c>, <c>['select-all']</c>, <c>['press', tooltip]</c>
/// (a toolbar button), <c>['shot', png]</c>, <c>['close']</c>.
/// <c>debug-signature-dialog</c> (<c>as</c>): <c>['new']</c>, <c>['edit', address, name]</c> (through the dialog service, the
/// result is logged as <c>signature-dialog: …</c>), <c>['name', text]</c>, <c>['body', html]</c>, <c>['bold-all']</c>,
/// <c>['state']</c>, <c>['shot', png]</c>, <c>['save']</c>, <c>['cancel']</c>.
/// </summary>
internal static class RichEditorDebugActions
{
    private static (Adw.Dialog Dialog, WinoRichTextEditorView Editor)? _open;

    public static void Register(Adw.Application application, Func<IServiceProvider?> services)
    {
        var editor = Gio.SimpleAction.New("debug-rich-editor", GLib.VariantType.New("as"));
        editor.OnActivate += (sender, args) => _ = RunEditorAsync(services, args.Parameter?.GetStrv(out _) ?? []);
        application.AddAction(editor);
        var signature = Gio.SimpleAction.New("debug-signature-dialog", GLib.VariantType.New("as"));
        signature.OnActivate += (sender, args) => _ = RunSignatureAsync(services, args.Parameter?.GetStrv(out _) ?? []);
        application.AddAction(signature);
    }

    private static async Task RunEditorAsync(Func<IServiceProvider?> services, string[] arguments)
    {
        try
        {
            var provider = services() ?? throw new InvalidOperationException("The services are not ready.");
            var command = Argument(arguments, 0);
            if (command == "open")
            {
                if (_open is not null) throw new InvalidOperationException("A rich editor is open already.");
                var editor = new WinoRichTextEditorView(new WinoRichTextEditorOptions(Tabbed: arguments.Length > 1 && arguments[1] == "tabbed", Table: true, Emoji: true, Highlight: true),
                    "Rich editor", provider.GetRequiredService<IDispatcher>(), provider.GetService<IExternalLauncher>(), provider.GetService<IPreferencesService>(),
                    provider.GetService<ITranslationService>(), exception => Serilog.Log.Error(exception, "Rich editor failed."));
                var dialog = Adw.Dialog.New();
                dialog.SetTitle("Rich editor");
                dialog.SetContentWidth(640);
                dialog.SetContentHeight(480);
                var box = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
                box.SetMarginTop(12);
                box.SetMarginBottom(12);
                box.SetMarginStart(12);
                box.SetMarginEnd(12);
                box.Append(editor.Root);
                var toolbar = Adw.ToolbarView.New();
                toolbar.AddTopBar(Adw.HeaderBar.New());
                toolbar.SetContent(box);
                dialog.SetChild(toolbar);
                _open = (dialog, editor);
                var owner = provider.GetRequiredService<GtkDialogService>().ResolveOwner() ?? throw new InvalidOperationException("No window.");
                dialog.Present(owner);
                await editor.LoadAsync("<p>Rich editor</p>", Adw.StyleManager.GetDefault().Dark);
                Log($"open ready={editor.IsReady}");
                return;
            }
            var (openDialog, openEditor) = _open ?? throw new InvalidOperationException("No rich editor is open.");
            switch (command)
            {
                case "set": await openEditor.SetHtmlAsync(Argument(arguments, 1)); break;
                case "get": Log("body " + await openEditor.GetHtmlBodyAsync()); return;
                case "select-all": await openEditor.SessionForDebug.DebugEvaluateAsync("document.execCommand('selectAll'), true"); break;
                case "press": PressTool(openEditor.Root, Argument(arguments, 1)); break;
                case "shot": DebugActions.Capture(openDialog, Argument(arguments, 1)); break;
                case "close":
                    _open = null;
                    openDialog.ForceClose();
                    await openEditor.DisposeAsync();
                    break;
                default: throw new ArgumentException($"'{command}' is not a debug-rich-editor command.");
            }
            Log("done " + command);
        }
        catch (Exception exception) { Serilog.Log.Error(exception, "Debug action debug-rich-editor failed."); }
    }

    private static async Task RunSignatureAsync(Func<IServiceProvider?> services, string[] arguments)
    {
        try
        {
            var provider = services() ?? throw new InvalidOperationException("The services are not ready.");
            var command = Argument(arguments, 0);
            switch (command)
            {
                case "new":
                    _ = ShowAsync(provider, null);
                    return;
                case "edit":
                {
                    var account = (await provider.GetRequiredService<IAccountService>().GetAccountsAsync())
                        .FirstOrDefault(a => string.Equals(a.Address, Argument(arguments, 1), StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException("No such account.");
                    var signature = (await provider.GetRequiredService<ISignatureService>().GetSignaturesAsync(account.Id))
                        .FirstOrDefault(s => s.Name == Argument(arguments, 2)) ?? throw new InvalidOperationException("No such signature.");
                    _ = ShowAsync(provider, signature);
                    return;
                }
            }
            var session = SignatureEditorDialog.DebugCurrent ?? throw new InvalidOperationException("No signature editor is open.");
            switch (command)
            {
                case "name": session.NameRow.SetText(Argument(arguments, 1)); break;
                case "body": await session.Editor.SetHtmlAsync(Argument(arguments, 1)); break;
                case "bold-all":
                    await session.Editor.SessionForDebug.DebugEvaluateAsync("document.execCommand('selectAll'), true");
                    await Task.Delay(300);
                    PressTool(session.Editor.Root, "Bold");
                    break;
                case "state":
                    Log($"state ready={session.Editor.IsReady} canSave={session.CanSave} saveSensitive={session.SaveButton.GetSensitive()} name={session.NameRow.GetText()}");
                    return;
                case "shot": DebugActions.Capture(session.SaveButton.GetRoot() as Gtk.Widget ?? throw new InvalidOperationException("Not shown."), Argument(arguments, 1)); break;
                case "save": await session.SaveAsync(); break;
                case "cancel": session.Cancel(); break;
                default: throw new ArgumentException($"'{command}' is not a debug-signature-dialog command.");
            }
            Log("done " + command);
        }
        catch (Exception exception) { Serilog.Log.Error(exception, "Debug action debug-signature-dialog failed."); }
    }

    private static async Task ShowAsync(IServiceProvider provider, AccountSignature? signature)
    {
        try
        {
            var result = await provider.GetRequiredService<IMailDialogService>().ShowSignatureEditorDialog(signature);
            Serilog.Log.Information("signature-dialog: {Result}", result is null ? "null"
                : $"name={result.Name} html.length={result.HtmlBody?.Length ?? 0} id={result.Id} account={result.MailAccountId} sameId={signature?.Id == result.Id} html={result.HtmlBody}");
        }
        catch (Exception exception) { Serilog.Log.Error(exception, "The signature dialog failed."); }
    }

    private static void PressTool(Gtk.Widget root, string tooltip)
    {
        var button = Find(root, tooltip) ?? throw new InvalidOperationException($"No tool '{tooltip}'.");
        button.Activate();
    }

    private static Gtk.Widget? Find(Gtk.Widget widget, string tooltip)
    {
        if (widget is Gtk.Button && widget.GetMapped() && (widget.GetTooltipText() ?? string.Empty).StartsWith(tooltip, StringComparison.Ordinal)) return widget;
        for (var child = widget.GetFirstChild(); child is not null; child = child.GetNextSibling())
        {
            if (Find(child, tooltip) is { } found) return found;
        }
        return null;
    }

    private static void Log(string line) => Serilog.Log.Information("rich-editor-debug: {Line}", line);

    private static string Argument(string[] arguments, int index)
        => index < arguments.Length ? arguments[index] : throw new ArgumentException($"Argument {index} is missing.");
}
#endif
