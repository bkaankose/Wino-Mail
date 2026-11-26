using System;
using System.Threading;
using System.Threading.Tasks;
using CoreGraphics;
using UIKit;
using Wino.Core.Domain;
using Wino.Editor;
using Wino.Renderer;
using Wino.Renderer.Editing;

namespace Wino.Mail.UIKit.Views.Mail.Compose;

/// <summary>
/// The composer's menu (Windows EngineMailEditorView context menu, Mac MacEngineEditor.ContextMenu): spelling
/// suggestions, editing and link commands, picture Resize and Describe, shown as an action sheet in the editor's theme.
/// </summary>
internal sealed partial class UIKitEngineEditor
{
    private const int MaximumSpellingSuggestions = 5;

    private bool _isDarkEditor;

    /// <summary>The controller that presents the menu; the compose page sets it to its top-most controller.</summary>
    public Func<UIViewController?>? PresentingController { get; set; }

    private void View_ContextMenuRequested(object? sender, ContextMenuRequest request)
    {
        var sheet = UIAlertController.Create(null, null, UIAlertControllerStyle.ActionSheet);
        sheet.OverrideUserInterfaceStyle = _isDarkEditor ? UIUserInterfaceStyle.Dark : UIUserInterfaceStyle.Light;

        if (request.IsEditable && request.MisspelledWord is not null)
        {
            foreach (var suggestion in request.SpellingSuggestions)
            {
                if (sheet.Actions.Length >= MaximumSpellingSuggestions) break;
                AddItem(sheet, suggestion, () => _view.ApplySpellingSuggestion(request, suggestion));
            }

            if (request.SpellingSuggestions.Count == 0)
                AddItem(sheet, Translator.Composer_SpellingNoSuggestions, null, enabled: false);

            AddItem(sheet, Translator.Composer_SpellingIgnore, () => _view.IgnoreSpelling(request));
            AddItem(sheet, Translator.Composer_SpellingAddToDictionary, () => _view.AddToSpellingDictionary(request));
        }

        if (request.IsEditable)
            AddItem(sheet, Translator.MacOSMenu_Cut, () => _view.Cut(), enabled: request.HasSelection);

        AddItem(sheet, Translator.Buttons_Copy, () => _view.CopySelection(), enabled: request.HasSelection);

        if (request.IsEditable)
        {
            AddItem(sheet, Translator.MacOSMenu_Paste, () => _view.Paste());
            AddItem(sheet, Translator.Composer_PasteAsPlainText, () => _view.PastePlainText());
        }

        if (request.IsEditable && request.OnImage)
        {
            AddItem(sheet, Translator.Composer_ResizePicture, () => RunPictureCommand(Engine.ResizeSelectedImageAsync));
            AddItem(sheet, Translator.Composer_PictureDescription, () => RunPictureCommand(Engine.DescribeSelectedImageAsync));
        }

        if (request.LinkUrl is { Length: > 0 } linkUrl && Uri.TryCreate(linkUrl, UriKind.Absolute, out var uri))
        {
            AddItem(sheet, Translator.Composer_OpenLink, () => _ = OpenLinkAsync(uri));
            if (request.IsEditable && !string.IsNullOrWhiteSpace(_view.EditState?.LinkUrl))
                AddItem(sheet, Translator.Composer_RemoveLink, () => _ = RemoveLinkAsync());
        }

        if (_view.CanExecute(EditCommandKind.MakeIslandEditable))
            AddItem(sheet, Translator.Composer_EditProtectedContent, () => _view.Execute(EditCommandKind.MakeIslandEditable));

        AddItem(sheet, Translator.MacOSMenu_SelectAll, () => _view.SelectAll());
        AddItem(sheet, Translator.Buttons_Cancel, null, style: UIAlertActionStyle.Cancel);

        if (PresentingController?.Invoke() is not { } presenter) return;
        if (sheet.PopoverPresentationController is { } popover)
        {
            popover.SourceView = _view;
            popover.SourceRect = new CGRect(request.ViewPoint.X, request.ViewPoint.Y, 1, 1);
        }

        presenter.PresentViewController(sheet, true, null);
    }

    private static void AddItem(UIAlertController sheet, string title, Action? action, bool enabled = true, UIAlertActionStyle style = UIAlertActionStyle.Default)
    {
        var item = UIAlertAction.Create(title, style, action is null ? null : _ => action());
        item.Enabled = enabled && action is not null || style == UIAlertActionStyle.Cancel;
        sheet.AddAction(item);
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
