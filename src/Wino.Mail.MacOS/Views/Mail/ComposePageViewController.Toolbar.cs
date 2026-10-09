using AppKit;
using MimeKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models;
using Wino.Editor;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Mail.MacOS.Views.Mail.Compose;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Mail;

/// <summary>
/// The composer's Format / Insert / Options editor toolbar (Windows EditorTabbedCommandBarControl with the
/// composer's InsertCustomContent and OptionsCustomContent), the editor theme following the appearance,
/// ⌘B / ⌘I / ⌘U, and the user's Send shortcut from Settings › Keyboard shortcuts.
/// </summary>
public sealed partial class ComposePageViewController
{
    private EditorFormatToolbar _formatToolbar = null!;
    private EditorFormatButton _themeButton = null!;
    private bool _darkEditor;
    private bool? _appearanceDark;
    private KeyboardShortcutSnapshot? _sendShortcutFallback;

    private NSView BuildEditorToolbar()
    {
        _formatToolbar = new EditorFormatToolbar(EditorToolbarLayout.Tabbed, new EditorToolbarFeatures(), ViewModel.PreferencesService, _translations, ReportError);

        // Insert: Attach files first (Windows InsertCustomContent).
        var attach = new EditorFormatButton(WinoIconGlyph.Attachment, Translator.Composer_AttachFiles, AttachFiles, Translator.Composer_AttachFilesDescription);
        _formatToolbar.AddInsertItems(new EditorToolbarItem(attach, () => new NSMenuItem(Translator.Composer_AttachFiles, (_, _) => AttachFiles())));

        // Options: importance, S/MIME, read receipt (Windows OptionsCustomContent), then the Mac editor theme and undo/redo.
        var importance = new EditorToolbarItem(_importance, () =>
        {
            var current = ViewModel.SelectedMessageImportance;
            var menu = new NSMenu(Translator.Composer_Importance) { AutoEnablesItems = false };
            foreach (var (title, value) in new[] { (Translator.Composer_HighImportance, MessageImportance.High), (Translator.Composer_NormalImportance, MessageImportance.Normal), (Translator.Composer_LowImportance, MessageImportance.Low) })
                menu.AddItem(new NSMenuItem(title, (_, _) => SetImportance(value)) { State = current == value ? NSCellStateValue.On : NSCellStateValue.Off });
            return new NSMenuItem(Translator.Composer_Importance) { Submenu = menu };
        });
        var items = new List<EditorToolbarItem> { importance };
        items.AddRange(BuildSecurityOptions());
        _themeButton = new EditorFormatButton(WinoIconGlyph.DarkEditor, Translator.Composer_DarkTheme, ToggleEditorTheme);
        items.Add(EditorToolbarItem.Divider());
        items.Add(new EditorToolbarItem(_themeButton, () => new NSMenuItem(ThemeLabel(_darkEditor), (_, _) => ToggleEditorTheme())));
        items.Add(new EditorToolbarItem(new EditorFormatButton(WinoIconGlyph.ArrowUndo, Translator.MacOSMenu_Undo, () => Run(EditorCommand.Undo()), $"{Translator.MacOSMenu_Undo} (⌘Z)"),
            () => new NSMenuItem(Translator.MacOSMenu_Undo, (_, _) => Run(EditorCommand.Undo()))));
        items.Add(new EditorToolbarItem(new EditorFormatButton(WinoIconGlyph.ArrowRedo, Translator.MacOSMenu_Redo, () => Run(EditorCommand.Redo()), $"{Translator.MacOSMenu_Redo} (⇧⌘Z)"),
            () => new NSMenuItem(Translator.MacOSMenu_Redo, (_, _) => Run(EditorCommand.Redo()))));
        _formatToolbar.AddOptionsItems(items.ToArray());

        var host = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(_formatToolbar, host, 8, 16, 8, 12);
        return host;
    }

    private void AttachFiles() => Observe(ViewModel.AttachFilesCommand.ExecuteAsync(null));

    private void DisposeToolbar() => _formatToolbar?.Detach();

    // ---- Editor theme ----

    private static string ThemeLabel(bool dark) => dark ? Translator.Composer_LightTheme : Translator.Composer_DarkTheme;

    private void ToggleEditorTheme()
    {
        if (_editor is null || _editorDisposed) return;
        _darkEditor = !_darkEditor;
        UpdateThemeButton();
        Observe(_editor.SetThemeAsync(_darkEditor));
    }

    /// <summary>Windows GetEditorThemeIcon / GetEditorThemeToolTip: the button offers the other theme.</summary>
    private void UpdateThemeButton()
    {
        if (_themeButton is null) return;
        _themeButton.SetGlyph(_darkEditor ? WinoIconGlyph.LightEditor : WinoIconGlyph.DarkEditor, ThemeLabel(_darkEditor));
        WinoAccessibility.Label(_themeButton.Button, ThemeLabel(_darkEditor));
    }

    /// <summary>
    /// Windows ApplicationThemeChanged: an appearance change (system or a custom theme setting the window
    /// appearance) re-themes the editor. Only real changes count, so moving the view between windows keeps
    /// a theme the user picked with the toggle.
    /// </summary>
    private void FollowAppearance()
    {
        if (!ViewLoaded) return;
        var dark = WinoIcons.IsDark(View.EffectiveAppearance);
        var previous = _appearanceDark;
        _appearanceDark = dark;
        if (previous is null || previous == dark || _editor is null || _editorDisposed) return;
        _darkEditor = dark;
        UpdateThemeButton();
        Observe(_editor.SetThemeAsync(dark));
    }

    // ---- Formatting keys ----

    /// <summary>⌘B, ⌘I and ⌘U in the body run the toolbar commands; WebKit's own handling is not relied on.</summary>
    private bool TryHandleFormattingKey(string? key, NSEventModifierMask flags, NSResponder? responder)
    {
        if (flags != NSEventModifierMask.CommandKeyMask || !IsInEditor(responder)) return false;
        EditorCommand? command = key switch
        {
            "b" => EditorCommand.ToggleBold(),
            "i" => EditorCommand.ToggleItalic(),
            "u" => EditorCommand.ToggleUnderline(),
            _ => null
        };
        if (command is null) return false;
        Run(command);
        return true;
    }

    // ---- Send shortcut ----

    private void BindSendShortcut()
    {
        // The service raises the event on the thread that saved the shortcut.
        EventHandler changed = (_, _) => NSApplication.SharedApplication.BeginInvokeOnMainThread(() => { if (!Bindings.IsDisposed) ApplySendShortcut(); });
        _shortcuts.KeyboardShortcutsChanged += changed;
        Bindings.Own(new ActionDisposable(() => _shortcuts.KeyboardShortcutsChanged -= changed));
        ApplySendShortcut();
    }

    /// <summary>
    /// The enabled Mail Send shortcut becomes the Send button's key equivalent (mapped like the main menu).
    /// One that a key equivalent cannot carry (no Command or Control) is matched by the key monitor instead.
    /// Without a custom shortcut Send keeps ⌘↩.
    /// </summary>
    private void ApplySendShortcut()
    {
        var key = "\r";
        var mask = NSEventModifierMask.CommandKeyMask;
        _sendShortcutFallback = null;
        var shortcut = _shortcuts.EnabledShortcutsSnapshot.FirstOrDefault(static item => item.Mode == WinoApplicationMode.Mail && item.Action == KeyboardShortcutAction.Send);
        if (shortcut is not null)
        {
            if (AppDelegate.TryGetKeyEquivalent(shortcut, out var customKey, out var customMask))
            {
                key = customKey;
                mask = customMask;
            }
            else
            {
                _sendShortcutFallback = shortcut;
            }
        }
        _sendButton.KeyEquivalent = key;
        _sendButton.KeyEquivalentModifierMask = mask;
        _sendButton.ToolTip = $"{Translator.Buttons_Send} ({ShortcutText(key, mask)})";
    }

    /// <summary>
    /// Send answers its key equivalent only while the focus is in the composer (Windows Compose /
    /// PopOutCompose input context), so a docked composer never sends from the mail list or reader.
    /// </summary>
    private bool IsSendKeyEquivalentInScope()
    {
        if (Bindings.IsDisposed || !ViewLoaded || View.Window is not { } window) return false;
        var responder = window.FirstResponder;
        // A popped-out composer owns its whole window.
        return IsInComposer(responder) || (_window is not null && ReferenceEquals(window, _window));
    }

    private sealed class ComposeSendButton(Func<bool> inScope) : NSButton
    {
        public override bool PerformKeyEquivalent(NSEvent theEvent) => inScope() && base.PerformKeyEquivalent(theEvent);
    }

    private static string ShortcutText(string key, NSEventModifierMask mask)
    {
        var text = string.Empty;
        if (mask.HasFlag(NSEventModifierMask.ControlKeyMask)) text += "⌃";
        if (mask.HasFlag(NSEventModifierMask.AlternateKeyMask)) text += "⌥";
        if (mask.HasFlag(NSEventModifierMask.ShiftKeyMask)) text += "⇧";
        if (mask.HasFlag(NSEventModifierMask.CommandKeyMask)) text += "⌘";
        return text + (key switch { "\r" => "↩", "\t" => "⇥", " " => "Space", _ => key.ToUpperInvariant() });
    }

    /// <summary>A Send shortcut without Command or Control (for example ⌥S), matched in the composer only.</summary>
    private bool MatchesSendFallback(NSEvent theEvent, NSEventModifierMask flags)
    {
        if (_sendShortcutFallback is not { } shortcut) return false;
        NSEventModifierMask expected = 0;
        if (shortcut.ModifierKeys.HasFlag(ModifierKeys.Alt)) expected |= NSEventModifierMask.AlternateKeyMask;
        if (shortcut.ModifierKeys.HasFlag(ModifierKeys.Shift)) expected |= NSEventModifierMask.ShiftKeyMask;
        // A bare key would send on the first matching letter typed in the body.
        if (expected == 0 || flags != expected) return false;

        var name = shortcut.Key?.Trim().ToUpperInvariant() ?? string.Empty;
        return name switch
        {
            "ENTER" or "RETURN" => theEvent.KeyCode is 36 or 76,
            { Length: 1 } single when char.IsLetterOrDigit(single[0])
                => string.Equals(theEvent.CharactersIgnoringModifiers, single, StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }
}
