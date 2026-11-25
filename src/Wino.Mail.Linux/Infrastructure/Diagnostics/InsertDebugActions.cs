#if DEBUG
using Wino.Editor;
using Wino.Mail.Linux.Views.Mail.Compose;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// <c>debug-insert</c> <c>as</c> on the active composer's Insert commands: <c>['link']</c>, <c>['ctrl-k']</c> (the editor's
/// openLinkDialog shortcut path), <c>['props']</c>, <c>['table-dialog']</c> (open the forms; fill them over AT-SPI),
/// <c>['confirm']</c> (the last form's primary button), <c>['picture', path]</c> (the next picker returns path, then the
/// Picture command runs), <c>['table', rows, columns]</c> (no form), <c>['table-op', addRow|removeRow|addColumn|removeColumn|deleteTable]</c>,
/// <c>['emoji', text]</c> (the chooser's emoji-picked handler), <c>['image-props', alt, url]</c> (no form), <c>['emoji-open']</c>, <c>['click-image']</c> (clicks the first image in the body), <c>['caret-cell']</c> (caret into the first table cell), <c>['type', text]</c> (WebKit's typing path at the end of the body).
/// </summary>
internal static class InsertDebugActions
{
    public static void Register(Adw.Application application)
    {
        var action = Gio.SimpleAction.New("debug-insert", GLib.VariantType.New("as"));
        action.OnActivate += (sender, args) => _ = RunAsync(args.Parameter?.GetStrv(out var _) ?? []);
        application.AddAction(action);
    }

    private static async Task RunAsync(string[] arguments)
    {
        try
        {
            var command = Argument(arguments, 0);
            var view = ComposeDebugActions.Current ?? throw new InvalidOperationException("No composer is shown.");
            var items = view.InsertItems ?? throw new InvalidOperationException("The composer has no Insert commands.");
            var toolbar = view.FormatToolbar ?? throw new InvalidOperationException("The composer has no toolbar.");
            var editor = view.Editor ?? throw new InvalidOperationException("No editor.");
            switch (command)
            {
                case "link": _ = items.OpenLinkDialogAsync(); break;
                case "ctrl-k": toolbar.DebugRaiseShortcut(EditorShortcutKind.OpenLinkDialog); break;
                case "props": _ = items.EditImagePropertiesAsync(); break;
                case "table-dialog": _ = items.InsertTableAsync(); break;
                case "confirm": (EditorDialogs.Current ?? throw new InvalidOperationException("No form is open.")).DebugConfirm(); break;
                case "picture":
                    Views.Dialogs.FilePickers.DebugNext.Enqueue(Argument(arguments, 1));
                    await items.InsertPicturesAsync();
                    break;
                case "table":
                    await toolbar.RunAsync(editor, EditorCommand.InsertTable(new EditorTableCommandArgs(int.Parse(Argument(arguments, 1)), int.Parse(Argument(arguments, 2)))));
                    break;
                case "table-op": Log($"table-op {arguments[1]}={await editor.TableCommandAsync(Argument(arguments, 1))} inTable={editor.IsInTable}"); break;
                case "click-image":
                    Log("click-image " + await editor.DebugEvaluateAsync("(()=>{const i=document.querySelector('#wino-editor img');if(!i)return 'none';i.dispatchEvent(new MouseEvent('click',{bubbles:true}));return 'ok';})()"));
                    await Task.Delay(300);
                    break;
                case "type":
                    await editor.FocusEditorAsync(true);
                    Log("type " + await editor.DebugEvaluateAsync("(()=>{const e=document.getElementById('wino-editor');const r=document.createRange();r.selectNodeContents(e);r.collapse(false);const s=getSelection();s.removeAllRanges();s.addRange(r);return document.execCommand('insertText',false," + System.Text.Json.JsonSerializer.Serialize(Argument(arguments, 1), Wino.Editor.EditorJsonContext.Default.String) + ");})()"));
                    await Task.Delay(300);
                    break;
                case "caret-cell":
                    Log("caret-cell " + await editor.DebugEvaluateAsync("(()=>{const c=document.querySelector('#wino-editor td');if(!c)return 'none';const r=document.createRange();r.selectNodeContents(c);r.collapse(true);const s=getSelection();s.removeAllRanges();s.addRange(r);document.dispatchEvent(new Event('selectionchange'));return 'ok';})()"));
                    await Task.Delay(300);
                    break;
                case "emoji": items.InsertEmoji(Argument(arguments, 1)); break;
                case "emoji-open": toolbar.AllItems.First(static item => item.Widget is Gtk.MenuButton { } button && button.GetPopover() is Gtk.EmojiChooser).Widget.Activate(); break;
                case "image-props":
                    await toolbar.RunAsync(editor, EditorCommand.SetImageProperties(new EditorImagePropertiesCommandArgs(Argument(arguments, 1), Argument(arguments, 2), true)));
                    break;
                default: throw new ArgumentException($"'{command}' is not a debug-insert command.");
            }
            var state = editor.CurrentState;
            Log($"done {command} inTable={editor.IsInTable} image={state.IsImageSelected} link={state.LinkUrl} selected={state.SelectedText}");
        }
        catch (Exception exception) { Serilog.Log.Error(exception, "Debug action debug-insert failed."); }
    }

    private static void Log(string line) => Serilog.Log.Information("insert-debug: {Line}", line);

    private static string Argument(string[] arguments, int index)
        => index < arguments.Length ? arguments[index] : throw new ArgumentException($"Argument {index} is missing.");
}
#endif
