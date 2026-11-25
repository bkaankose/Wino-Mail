using Android.Views;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Editor;
using Wino.Mail.Android.Rendering;
using Wino.Renderer;
using Wino.Renderer.Editing;

namespace Wino.Mail.Android.Views.Mail.Compose;

/// <summary>
/// The composer's menu over the editor (Windows EngineMailEditorView.ContextMenu): spelling suggestions on a misspelled
/// word, cut, copy, paste, paste as plain text, the picture commands, the link commands and select all, in the editor's
/// theme.
/// </summary>
public sealed partial class ComposeFragment
{
    private const int MaximumSpellingSuggestions = 5;

    private void ShowEditorMenu(ContextMenuRequest request)
    {
        if (_web is not { } view || _editor is not { } editor || _editorDisposed) return;

        var rows = new List<EngineMenuEntry?>();
        if (request.IsEditable && request.MisspelledWord is not null)
        {
            foreach (var suggestion in request.SpellingSuggestions.Take(MaximumSpellingSuggestions))
                rows.Add(new EngineMenuEntry(suggestion, null, () => view.ApplySpellingSuggestion(request, suggestion), AutomationId: "EditorContextSpellingSuggestion"));
            if (request.SpellingSuggestions.Count == 0)
                rows.Add(new EngineMenuEntry(Translator.Composer_SpellingNoSuggestions, null, () => { }, IsEnabled: false, AutomationId: "EditorContextNoSpellingSuggestions"));
            rows.Add(new EngineMenuEntry(Translator.Composer_SpellingIgnore, WinoIconGlyph.Ignore, () => view.IgnoreSpelling(request), AutomationId: "EditorContextIgnoreSpelling"));
            rows.Add(new EngineMenuEntry(Translator.Composer_SpellingAddToDictionary, WinoIconGlyph.Checkmark, () => view.AddToSpellingDictionary(request), AutomationId: "EditorContextAddToDictionary"));
            rows.Add(null);
        }

        if (request.IsEditable)
            rows.Add(new EngineMenuEntry(Translator.MacOSMenu_Cut, null, () => view.Cut(), request.HasSelection, "EditorContextCut"));
        rows.Add(new EngineMenuEntry(Translator.Buttons_Copy, WinoIconGlyph.Copy, () => view.CopySelection(), request.HasSelection, "EditorContextCopy"));
        if (request.IsEditable)
        {
            rows.Add(new EngineMenuEntry(Translator.MacOSMenu_Paste, WinoIconGlyph.ClipboardPaste, () => view.Paste(), AutomationId: "EditorContextPaste"));
            rows.Add(new EngineMenuEntry(Translator.Composer_PasteAsPlainText, null, () => view.PastePlainText(), AutomationId: "EditorContextPastePlainText"));
        }

        if (request.IsEditable && request.OnImage)
        {
            rows.Add(null);
            rows.Add(new EngineMenuEntry(Translator.Composer_ResizePicture, WinoIconGlyph.ImageResize,
                () => Observe(editor.ResizeSelectedImageAsync()), AutomationId: "EditorContextResizePicture"));
            rows.Add(new EngineMenuEntry(Translator.Composer_PictureDescription, WinoIconGlyph.TextDescription,
                () => Observe(editor.DescribeSelectedImageAsync()), AutomationId: "EditorContextPictureDescription"));
        }

        if (request.LinkUrl is { Length: > 0 } linkUrl)
        {
            rows.Add(null);
            rows.Add(new EngineMenuEntry(Translator.Composer_OpenLink, WinoIconGlyph.Open, () => OpenLink(linkUrl), AutomationId: "EditorContextOpenLink"));
            if (request.IsEditable && !string.IsNullOrWhiteSpace(view.EditState?.LinkUrl))
                rows.Add(new EngineMenuEntry(Translator.Composer_RemoveLink, WinoIconGlyph.LinkDismiss,
                    () => Observe(editor.ExecuteCommandAsync(EditorCommand.RemoveLink())), AutomationId: "EditorContextRemoveLink"));
        }

        if (view.CanExecute(EditCommandKind.MakeIslandEditable))
        {
            rows.Add(null);
            rows.Add(new EngineMenuEntry(Translator.Composer_EditProtectedContent, WinoIconGlyph.Edit,
                () => view.Execute(EditCommandKind.MakeIslandEditable), AutomationId: "EditorContextEditProtectedContent"));
        }

        rows.Add(null);
        rows.Add(new EngineMenuEntry(Translator.MacOSMenu_SelectAll, WinoIconGlyph.SelectAll, () => view.SelectAll(), AutomationId: "EditorContextSelectAll"));

        if (view.Parent is FrameLayout host)
            EngineContextMenu.Show(host, request.ViewPoint, DisplayDensity(), _darkEditor, rows);
    }

    private float DisplayDensity() => Context?.Resources?.DisplayMetrics?.Density ?? 1f;

    private void OpenLink(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && MainApplication.Services?.GetService(typeof(IExternalLauncher)) is IExternalLauncher launcher)
            Observe(launcher.LaunchUriAsync(uri));
    }
}
