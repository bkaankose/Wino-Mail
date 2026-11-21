#if DEBUG
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Interfaces;
using Wino.Editor;
using Wino.Editor.WebKitGtk;
using Wino.Presentation.Bindings;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// <c>debug-editor</c> <c>as</c> drives a bare editor session in a window "Editor session": <c>['open']</c>,
/// <c>['set', html]</c>, <c>['get']</c> (logs the body), <c>['exec', bold|italic|underline|ul|ol|emoji]</c>,
/// <c>['exec', 'link', url]</c>, <c>['exec', 'table', '3x2']</c>, <c>['table', addRow|…]</c>, <c>['select-all']</c>,
/// <c>['paste-image', png]</c>, <c>['theme', light|dark]</c>, <c>['kill-web-process']</c>, <c>['close']</c>.
/// Session events are logged as <c>editor-debug: …</c>.
/// </summary>
internal static class EditorDebugActions
{
    private static EditorDebugWindow? _current;

    public static void Register(Adw.Application application, Func<IServiceProvider?> services)
    {
        var action = Gio.SimpleAction.New("debug-editor", GLib.VariantType.New("as"));
        action.OnActivate += (sender, args) =>
        {
            var arguments = args.Parameter?.GetStrv(out var _) ?? [];
            _ = RunAsync(application, services, arguments);
        };
        application.AddAction(action);
    }

    private static async Task RunAsync(Adw.Application application, Func<IServiceProvider?> services, string[] arguments)
    {
        try
        {
            var command = Argument(arguments, 0);
            if (command == "open")
            {
                if (_current is not null) throw new InvalidOperationException("The editor window is already open.");
                var provider = services() ?? throw new InvalidOperationException("The services are not ready.");
                _current = new EditorDebugWindow(application, provider.GetRequiredService<IDispatcher>(), provider.GetRequiredService<IExternalLauncher>());
                await _current.OpenAsync();
                Log("ready");
                return;
            }
            var window = _current ?? throw new InvalidOperationException("No editor window is open.");
            var session = window.Session;
            switch (command)
            {
                case "set": await session.RenderHtmlAsync(Argument(arguments, 1)); break;
                case "get": Log("body " + (await session.GetHtmlBodyAsync() ?? string.Empty)); break;
                case "exec": await session.ExecuteCommandAsync(Command(arguments)); break;
                case "table": Log($"table {Argument(arguments, 1)}={await session.TableCommandAsync(Argument(arguments, 1))}"); break;
                case "select-all":
                    await session.FocusEditorAsync(true);
                    session.View.ExecuteEditingCommand("SelectAll");
                    break;
                case "paste-image":
                    using (var texture = Gdk.Texture.NewFromFilename(Argument(arguments, 1)))
                        session.View.GetClipboard().SetTexture(texture);
                    Log($"paste-image inserted={await session.PasteClipboardImageAsync()}");
                    break;
                case "theme": await session.SetThemeAsync(Argument(arguments, 1) == "dark"); break;
                case "kill-web-process": session.View.TerminateWebProcess(); break;
                case "close":
                    _current = null;
                    await window.CloseAsync();
                    break;
                default: throw new ArgumentException($"'{command}' is not a debug-editor command.");
            }
            Log("done " + command);
        }
        catch (Exception exception) { Serilog.Log.Error(exception, "Debug action debug-editor failed."); }
    }

    private static EditorCommand Command(string[] arguments) => Argument(arguments, 1) switch
    {
        "bold" => EditorCommand.ToggleBold(),
        "italic" => EditorCommand.ToggleItalic(),
        "underline" => EditorCommand.ToggleUnderline(),
        "ul" => EditorCommand.ToggleUnorderedList(),
        "ol" => EditorCommand.ToggleOrderedList(),
        "emoji" => EditorCommand.InsertEmoji(),
        "link" => EditorCommand.InsertLink(new EditorLinkCommandArgs(Argument(arguments, 2), Argument(arguments, 2))),
        "table" when Argument(arguments, 2).Split('x') is [var rows, var columns]
            => EditorCommand.InsertTable(new EditorTableCommandArgs(int.Parse(rows), int.Parse(columns))),
        var other => throw new ArgumentException($"'{other}' is not an editor command.")
    };

    internal static void Log(string line) => Serilog.Log.Information("editor-debug: {Line}", line);

    private static string Argument(string[] arguments, int index)
        => index < arguments.Length ? arguments[index] : throw new ArgumentException($"Argument {index} is missing.");

    private sealed class EditorDebugWindow
    {
        private readonly Adw.Window _window;
        private readonly Gtk.Label _status = Gtk.Label.New(string.Empty);
        private readonly BindingScope _scope = new();

        public EditorDebugWindow(Adw.Application application, IDispatcher dispatcher, IExternalLauncher launcher)
        {
            WinoLiveObjects.Track(this);
            Session = new WebKitGtkHtmlMailEditorSession(dispatcher);
            Session.Configure(launcher);
            Session.View.SetVexpand(true);
            Session.StateChanged += (_, state) => Log($"StateChanged bold={state.IsBold} italic={state.IsItalic} ul={state.IsUnorderedList} inTable={Session.IsInTable} selection={state.HasSelection}");
            Session.ContentChanged += (_, _) => Log("ContentChanged");
            Session.ApplicationShortcutRequested += (_, gesture) => Log($"ApplicationShortcutRequested {gesture}");
            Session.OperationFailed += (_, error) => Log("OperationFailed " + error.Message);
            Session.Restarted += (_, _) => Log($"Restarted IsContentLost={Session.IsContentLost}");
            var box = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
            box.Append(Adw.HeaderBar.New());
            box.Append(Session.View);
            box.Append(_status);
            _window = Adw.Window.New();
            _window.SetTitle("Editor session");
            _window.SetApplication(application);
            _window.SetDefaultSize(640, 480);
            _window.SetContent(box);
            _scope.Own(GtkSignals.ConnectReturning<Gtk.Window, bool>(h => _window.OnCloseRequest += h, h => _window.OnCloseRequest -= h, (_, _) =>
            {
                if (_current == this) _current = null;
                _ = CloseAsync();
                return true;
            }, false));
        }

        public WebKitGtkHtmlMailEditorSession Session { get; }

        public async Task OpenAsync()
        {
            _window.Present();
            var started = DateTime.UtcNow;
            await Session.InitializeAsync();
            await Session.SetThemeAsync(Adw.StyleManager.GetDefault().Dark);
            _status.SetText($"ready in {(DateTime.UtcNow - started).TotalMilliseconds:0} ms");
            Log(_status.GetText());
        }

        public async Task CloseAsync()
        {
            if (_scope.IsDisposed) return;
            _scope.Dispose();
            await Session.DisposeAsync();
            _window.SetContent(null);
            _window.Destroy();
            Log("closed");
        }
    }
}
#endif
