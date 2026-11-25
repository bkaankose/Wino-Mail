using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AppKit;
using CoreGraphics;
using Wino.Core.Domain;
using Wino.Editor;
using Wino.Renderer;
using Wino.Renderer.Editing;
using Wino.Renderer.Selection;

namespace Wino.Mail.MacOS.Views.Mail.Compose;

/// <summary>
/// The composer's context menu (Windows EngineMailEditorView context menu): spelling suggestions, editing and link
/// commands, picture Resize and Describe, drawn in the editor's own theme rather than the app's.
/// </summary>
public sealed partial class MacEngineEditor
{
    private const int MaximumSpellingSuggestions = 5;

    private bool _isDarkEditor;

    private void View_ContextMenuRequested(object? sender, ContextMenuRequest request)
    {
        var menu = new NSMenu { AutoEnablesItems = false };
        menu.Appearance = NSAppearance.GetAppearance(_isDarkEditor ? NSAppearance.NameDarkAqua : NSAppearance.NameAqua);

        if (request.IsEditable && request.MisspelledWord is not null)
        {
            foreach (var suggestion in request.SpellingSuggestions.Take(MaximumSpellingSuggestions))
                menu.AddItem(MenuItem(suggestion, () => _view.ApplySpellingSuggestion(request, suggestion)));

            if (request.SpellingSuggestions.Count == 0)
                menu.AddItem(MenuItem(Translator.Composer_SpellingNoSuggestions, null));

            menu.AddItem(MenuItem(Translator.Composer_SpellingIgnore, () => _view.IgnoreSpelling(request)));
            menu.AddItem(MenuItem(Translator.Composer_SpellingAddToDictionary, () => _view.AddToSpellingDictionary(request)));
            menu.AddItem(NSMenuItem.SeparatorItem);
        }

        if (request.IsEditable)
            menu.AddItem(MenuItem(Translator.MacOSMenu_Cut, () => _view.Cut(), request.HasSelection));

        menu.AddItem(MenuItem(Translator.Buttons_Copy, () => _view.CopySelection(), request.HasSelection));

        if (request.IsEditable)
        {
            menu.AddItem(MenuItem(Translator.MacOSMenu_Paste, () => _view.Paste()));
            menu.AddItem(MenuItem(Translator.Composer_PasteAsPlainText, () => _view.PastePlainText()));
        }

        if (request.IsEditable && request.OnImage)
        {
            menu.AddItem(NSMenuItem.SeparatorItem);
            menu.AddItem(MenuItem(Translator.Composer_ResizePicture, () => RunPictureCommand(Engine.ResizeSelectedImageAsync)));
            menu.AddItem(MenuItem(Translator.Composer_PictureDescription, () => RunPictureCommand(Engine.DescribeSelectedImageAsync)));
        }

        if (request.LinkUrl is { Length: > 0 } linkUrl)
        {
            menu.AddItem(NSMenuItem.SeparatorItem);
            if (Uri.TryCreate(linkUrl, UriKind.Absolute, out var uri))
                menu.AddItem(MenuItem(Translator.Composer_OpenLink, () => _ = OpenLinkAsync(uri)));

            if (request.IsEditable && !string.IsNullOrWhiteSpace(_view.EditState?.LinkUrl))
                menu.AddItem(MenuItem(Translator.Composer_RemoveLink, () => _ = RemoveLinkAsync()));
        }

        if (_view.CanExecute(EditCommandKind.MakeIslandEditable))
        {
            menu.AddItem(NSMenuItem.SeparatorItem);
            menu.AddItem(MenuItem(Translator.Composer_EditProtectedContent, () => _view.Execute(EditCommandKind.MakeIslandEditable)));
        }

        menu.AddItem(NSMenuItem.SeparatorItem);
        menu.AddItem(MenuItem(Translator.MacOSMenu_SelectAll, () => _view.SelectAll()));

        menu.PopUpMenu(null, new CGPoint(request.ViewPoint.X, request.ViewPoint.Y), _view);
    }

    private static NSMenuItem MenuItem(string title, Action? action, bool isEnabled = true)
    {
        var item = new NSMenuItem(title, (_, _) => action?.Invoke()) { Enabled = isEnabled && action is not null };
        return item;
    }

    private async void RunPictureCommand(Func<CancellationToken, Task> command)
    {
        try
        {
            await command(CancellationToken.None);
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception exception)
        {
            OperationFailed?.Invoke(this, exception);
        }
    }

    private async Task RemoveLinkAsync()
    {
        try
        {
            await Engine.ExecuteCommandAsync(EditorCommand.RemoveLink());
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
