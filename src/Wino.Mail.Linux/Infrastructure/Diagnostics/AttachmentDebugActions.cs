#if DEBUG
using Wino.Mail.Linux.Views.Mail.Compose;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// <c>debug-attach</c> <c>as</c> on the active composer: <c>['files', path…]</c> (the picker's result path), <c>['picker', path]</c>
/// (the Attach command with the next picker answering path), <c>['drop', files|images|none, path…]</c> (shows the zones, then
/// runs the drop handler with a real GdkFileList at the zone's centre; the signal itself needs a real drag),
/// <c>['overlay', off|on|files|images]</c>, <c>['paste-image', png]</c> (puts the PNG on the clipboard as a texture and runs the
/// Ctrl+V path), <c>['paste-text', text]</c> (text on the clipboard; the Ctrl+V path must not take it), <c>['dump', path]</c>,
/// <c>['remove', index]</c>, <c>['remove-all']</c>.
/// </summary>
internal static class AttachmentDebugActions
{
    public static void Register(Adw.Application application)
    {
        var action = Gio.SimpleAction.New("debug-attach", GLib.VariantType.New("as"));
        action.OnActivate += (sender, args) => _ = RunAsync(args.Parameter?.GetStrv(out var _) ?? []);
        application.AddAction(action);
    }

    private static async Task RunAsync(string[] arguments)
    {
        try
        {
            var command = Argument(arguments, 0);
            var view = ComposeDebugActions.Current ?? throw new InvalidOperationException("No composer is shown.");
            var vm = view.ViewModel;
            var overlay = view.DropOverlay ?? throw new InvalidOperationException("The composer has no drop zones.");
            switch (command)
            {
                case "files": await view.AttachPathsAsync(arguments[1..]); break;
                case "picker":
                    Views.Dialogs.FilePickers.DebugNext.Enqueue(Argument(arguments, 1));
                    await vm.AttachFilesCommand.ExecuteAsync(null);
                    break;
                case "overlay":
                    var mode = Argument(arguments, 1);
                    overlay.SetPreview(mode != "off", mode switch { "files" => ComposeDropZone.Files, "images" => ComposeDropZone.Images, _ => ComposeDropZone.None });
                    break;
                case "drop":
                {
                    var zone = Argument(arguments, 1) switch { "files" => ComposeDropZone.Files, "images" => ComposeDropZone.Images, _ => ComposeDropZone.None };
                    overlay.SetPreview(true, zone);
                    await Task.Delay(300);
                    var (x, y) = view.DebugZoneCentre(zone);
                    var files = arguments[2..].Select(static path => Gio.FileHelper.NewForPath(path)).ToArray();
                    var list = Gdk.FileList.NewFromArray(files, (nuint)files.Length);
                    using var value = new GObject.Value(Gdk.FileList.GetGType());
                    value.SetBoxed(list.Handle.DangerousGetHandle());
                    Log($"drop accepted={overlay.Target.Drop(value, x, y)} zone={zone} at={x:0},{y:0} hit={overlay.ZoneAt(x, y)} active={overlay.IsActive}");
                    await Task.Delay(500);
                    break;
                }
                case "paste-image":
                {
                    var editor = view.Editor ?? throw new InvalidOperationException("No editor.");
                    var texture = Textures.FromFile(Argument(arguments, 1)) ?? throw new InvalidOperationException("The PNG could not be read.");
                    editor.View.GetClipboard().SetTexture(texture);
                    await Task.Delay(200);
                    Log($"paste-image consumed={view.PasteImageIfOnly()} imageOnly={editor.ClipboardOffersImageOnly}");
                    await Task.Delay(800);
                    break;
                }
                case "paste-text":
                {
                    var editor = view.Editor ?? throw new InvalidOperationException("No editor.");
                    editor.View.GetClipboard().SetText(Argument(arguments, 1));
                    await Task.Delay(200);
                    Log($"paste-text consumed={view.PasteImageIfOnly()} imageOnly={editor.ClipboardOffersImageOnly}");
                    break;
                }
                case "dump": DebugActions.WriteText(Argument(arguments, 1), (view.AttachmentTray?.Describe() ?? "no tray") + $"\noverlay active={overlay.IsActive} zone={overlay.HoveredZone} vm={vm.IsDraggingOverComposerGrid}/{vm.IsDraggingOverFilesDropZone}/{vm.IsDraggingOverImagesDropZone}\n"); break;
                case "remove": vm.RemoveAttachmentCommand.Execute(vm.IncludedAttachments[int.Parse(Argument(arguments, 1))]); break;
                case "remove-all": vm.RemoveAllAttachmentsCommand.Execute(null); break;
                default: throw new ArgumentException($"'{command}' is not a debug-attach command.");
            }
            Log($"done {command} attachments={vm.IncludedAttachments.Count}");
        }
        catch (Exception exception) { Serilog.Log.Error(exception, "Debug action debug-attach failed."); }
    }

    private static void Log(string line) => Serilog.Log.Information("attach-debug: {Line}", line);

    private static string Argument(string[] arguments, int index)
        => index < arguments.Length ? arguments[index] : throw new ArgumentException($"Argument {index} is missing.");
}
#endif
