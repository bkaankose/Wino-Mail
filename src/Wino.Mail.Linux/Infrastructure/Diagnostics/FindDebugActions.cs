#if DEBUG
using Wino.Editor.WebKitGtk;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// On the active composer. <c>debug-find</c> <c>as</c>: <c>['open'(, 'replace')]</c>, <c>['ctrl-f']</c> (the app.search path),
/// <c>['query', text]</c> (types into the entry), <c>['next']</c>, <c>['prev']</c>, <c>['options', ignoreCase, wholeWords]</c>,
/// <c>['replace', text]</c>, <c>['replace-all', text]</c>, <c>['close']</c>, <c>['state', path]</c>.
/// <c>debug-spell</c> <c>as</c>: <c>['on'|'off']</c>, <c>['lang', code]</c>, <c>['state', path]</c>, <c>['menu'(, image)]</c> (the
/// editor's context-menu policy over WebKit's full stock menu; image puts a PNG on the clipboard first).
/// <c>debug-editor-theme</c> <c>s</c>: <c>light</c>, <c>dark</c>, <c>toggle</c> (the Options button).
/// </summary>
internal static class FindDebugActions
{
    public static void Register(Adw.Application application)
    {
        var find = Gio.SimpleAction.New("debug-find", GLib.VariantType.New("as"));
        find.OnActivate += (sender, args) => _ = FindAsync(args.Parameter?.GetStrv(out var _) ?? []);
        application.AddAction(find);
        var spell = Gio.SimpleAction.New("debug-spell", GLib.VariantType.New("as"));
        spell.OnActivate += (sender, args) => _ = SpellAsync(args.Parameter?.GetStrv(out var _) ?? []);
        application.AddAction(spell);
        var theme = Gio.SimpleAction.New("debug-editor-theme", GLib.VariantType.New("s"));
        theme.OnActivate += (sender, args) =>
        {
            try
            {
                var view = ComposeDebugActions.Current ?? throw new InvalidOperationException("No composer is shown.");
                switch (args.Parameter?.GetString(out _))
                {
                    case "light": view.ApplyEditorTheme(false); break;
                    case "dark": view.ApplyEditorTheme(true); break;
                    default: view.ToggleEditorTheme(); break;
                }
                Log($"editor-theme dark={view.IsEditorDark}");
            }
            catch (Exception exception) { Serilog.Log.Error(exception, "Debug action debug-editor-theme failed."); }
        };
        application.AddAction(theme);
    }

    private static async Task FindAsync(string[] arguments)
    {
        try
        {
            var command = Argument(arguments, 0);
            var view = ComposeDebugActions.Current ?? throw new InvalidOperationException("No composer is shown.");
            var bar = view.FindBar ?? throw new InvalidOperationException("No find bar.");
            switch (command)
            {
                case "open": view.ShowFind(arguments.Length > 1 && arguments[1] == "replace"); break;
                case "ctrl-f": Log($"ctrl-f handled={FindInPageTargets.TryOpen(view.Root.GetRoot() as Gtk.Window)}"); break;
                case "query":
                    ((Gtk.Editable)bar.QueryEntry).SetText(Argument(arguments, 1));
                    await Task.Delay(500);
                    break;
                case "next": await view.RunFindAsync(EditorFindDirection.Next); break;
                case "prev": await view.RunFindAsync(EditorFindDirection.Previous); break;
                case "options":
                    bar.SetOptions(bool.Parse(Argument(arguments, 1)), bool.Parse(Argument(arguments, 2)));
                    await Task.Delay(400);
                    break;
                case "replace":
                    bar.SetReplacement(Argument(arguments, 1));
                    await view.ReplaceAsync();
                    break;
                case "replace-all":
                    bar.SetReplacement(Argument(arguments, 1));
                    await view.ReplaceAllAsync();
                    break;
                case "close": view.CloseFind(); break;
                case "state":
                    DebugActions.WriteText(Argument(arguments, 1), $"open={bar.IsOpen} replace={bar.ShowsReplace} query={bar.Query} counter={bar.CounterText} ignoreCase={bar.IgnoreCase} wholeWords={bar.WholeWords}\n");
                    break;
                default: throw new ArgumentException($"'{command}' is not a debug-find command.");
            }
            Log($"find done {command} counter={bar.CounterText}");
        }
        catch (Exception exception) { Serilog.Log.Error(exception, "Debug action debug-find failed."); }
    }

    private static async Task SpellAsync(string[] arguments)
    {
        try
        {
            var command = Argument(arguments, 0);
            var view = ComposeDebugActions.Current ?? throw new InvalidOperationException("No composer is shown.");
            var editor = view.Editor ?? throw new InvalidOperationException("No editor.");
            var preferences = view.ViewModel.PreferencesService;
            switch (command)
            {
                case "on": view.SetSpellCheck(true, preferences.ComposerSpellCheckLanguageCode ?? string.Empty); break;
                case "off": view.SetSpellCheck(false, preferences.ComposerSpellCheckLanguageCode ?? string.Empty); break;
                case "lang": view.SetSpellCheck(true, Argument(arguments, 1)); break;
                case "state":
                    await Task.Delay(300);
                    var js = await editor.DebugEvaluateAsync("(()=>{const e=document.getElementById('wino-editor');return e.spellcheck+'|'+(e.getAttribute('lang')||document.documentElement.lang||'');})()");
                    DebugActions.WriteText(Argument(arguments, 1), $"enabled={WebKitGtkSpellCheck.IsEnabled} languages={string.Join(",", WebKitGtkSpellCheck.EffectiveLanguages())} js={js} pref={preferences.IsComposerSpellCheckEnabled}/{preferences.ComposerSpellCheckLanguageCode}\n");
                    break;
                case "menu":
                    if (arguments.Length > 1 && Wino.Presentation.Linux.Textures.FromFile(arguments[1]) is { } texture) editor.View.GetClipboard().SetTexture(texture);
                    await Task.Delay(200);
                    Log("menu " + editor.DebugContextMenu(
                        WebKit.ContextMenuAction.SpellingGuess, WebKit.ContextMenuAction.SpellingGuess, WebKit.ContextMenuAction.IgnoreSpelling,
                        WebKit.ContextMenuAction.LearnSpelling, WebKit.ContextMenuAction.OpenLink, WebKit.ContextMenuAction.OpenLinkInNewWindow,
                        WebKit.ContextMenuAction.DownloadLinkToDisk, WebKit.ContextMenuAction.GoBack, WebKit.ContextMenuAction.Reload,
                        WebKit.ContextMenuAction.InspectElement, WebKit.ContextMenuAction.Cut, WebKit.ContextMenuAction.Copy, WebKit.ContextMenuAction.Paste,
                        WebKit.ContextMenuAction.PasteAsPlainText, WebKit.ContextMenuAction.CopyImageUrlToClipboard, WebKit.ContextMenuAction.SelectAll,
                        WebKit.ContextMenuAction.InsertEmoji, WebKit.ContextMenuAction.InputMethods));
                    break;
                default: throw new ArgumentException($"'{command}' is not a debug-spell command.");
            }
            Log($"spell done {command}");
        }
        catch (Exception exception) { Serilog.Log.Error(exception, "Debug action debug-spell failed."); }
    }

    private static void Log(string line) => Serilog.Log.Information("find-debug: {Line}", line);

    private static string Argument(string[] arguments, int index)
        => index < arguments.Length ? arguments[index] : throw new ArgumentException($"Argument {index} is missing.");
}
#endif
