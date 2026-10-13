using MimeKit;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Common;
using Wino.Editor;
using Wino.Mail.UIKit.Views.Mail.Compose;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Mail;

/// <summary>
/// The composer's Format / Insert / Options bar above the keyboard (Mac Toolbar partial, Windows
/// EditorTabbedCommandBarControl with the composer's InsertCustomContent and OptionsCustomContent), and the editor
/// theme following the appearance.
/// </summary>
public sealed partial class ComposePageViewController
{
    private EditorFormatToolbar? _formatToolbar;
    private EditorToolbarButton? _themeButton;
    private bool _darkEditor;
    private bool? _appearanceDark;

    private void BuildEditorToolbar()
    {
        var toolbar = new EditorFormatToolbar(ViewModel.PreferencesService, _translations, ReportError, () => Presenter, PickInlineImagesAsync,
            () => _webView.EndEditing(true));
        _formatToolbar = toolbar;

        // Insert: Attach files first (Windows InsertCustomContent).
        toolbar.AddInsertItems(new EditorToolbarItem(new EditorToolbarButton(WinoIconGlyph.Attachment, Translator.Composer_AttachFiles, AttachFiles)));

        // Options: importance, S/MIME, read receipt (Windows OptionsCustomContent), then the editor theme and undo/redo.
        var items = new List<EditorToolbarItem>
        {
            new(new EditorToolbarButton(WinoIconGlyph.Important, Translator.Composer_Importance, null, () => [ImportanceMenu()]))
        };
        items.AddRange(BuildSecurityOptions());
        _themeButton = new EditorToolbarButton(WinoIconGlyph.DarkEditor, Translator.Composer_DarkTheme, ToggleEditorTheme);
        items.Add(EditorToolbarItem.Divider());
        items.Add(new EditorToolbarItem(_themeButton));
        items.Add(new EditorToolbarItem(new EditorToolbarButton(WinoIconGlyph.ArrowUndo, Translator.MacOSMenu_Undo, () => Run(EditorCommand.Undo()))));
        items.Add(new EditorToolbarItem(new EditorToolbarButton(WinoIconGlyph.ArrowRedo, Translator.MacOSMenu_Redo, () => Run(EditorCommand.Redo()))));
        toolbar.AddOptionsItems(items.ToArray());
        _webView.Accessory = toolbar;
    }

    /// <summary>High, normal and low importance with the current one checked.</summary>
    private UIMenu ImportanceMenu()
    {
        var current = ViewModel.SelectedMessageImportance;
        return EditorFormatToolbar.Inline(new[]
        {
            (Translator.Composer_HighImportance, MessageImportance.High),
            (Translator.Composer_NormalImportance, MessageImportance.Normal),
            (Translator.Composer_LowImportance, MessageImportance.Low)
        }.Select(item => (UIMenuElement)EditorFormatToolbar.Action(item.Item1, () => { SetImportance(item.Item2); ScheduleAutosave(); }, current == item.Item2)), Translator.Composer_Importance);
    }

    private async Task<List<SharedFile>> PickInlineImagesAsync()
        => _dialogs is { } dialogs ? await dialogs.PickFilesAsync(EditorDialogs.ImageFilters) ?? [] : [];

    private void DisposeToolbar()
    {
        _formatToolbar?.Detach();
        _webView.Accessory = null;
    }

    // ---- Editor theme ----

    private static string ThemeLabel(bool dark) => dark ? Translator.Composer_LightTheme : Translator.Composer_DarkTheme;

    private void ToggleEditorTheme()
    {
        if (_editor is not { } editor || _editorDisposed) return;
        _darkEditor = !_darkEditor;
        UpdateThemeButton();
        Observe(editor.SetThemeAsync(_darkEditor));
    }

    /// <summary>Windows GetEditorThemeIcon / GetEditorThemeToolTip: the button offers the other theme.</summary>
    private void UpdateThemeButton()
        => _themeButton?.SetGlyph(_darkEditor ? WinoIconGlyph.LightEditor : WinoIconGlyph.DarkEditor, ThemeLabel(_darkEditor));

    /// <summary>
    /// Windows ApplicationThemeChanged: a light or dark change re-themes the editor. Only real changes count, so a
    /// theme picked with the toggle stays until the appearance changes.
    /// </summary>
    private void FollowAppearance()
    {
        if (!IsViewLoaded) return;
        var dark = WinoTraits.IsDark(TraitCollection);
        var previous = _appearanceDark;
        _appearanceDark = dark;
        if (previous is null || previous == dark || _editor is not { } editor || _editorDisposed) return;
        _darkEditor = dark;
        UpdateThemeButton();
        Observe(editor.SetThemeAsync(dark));
    }
}
