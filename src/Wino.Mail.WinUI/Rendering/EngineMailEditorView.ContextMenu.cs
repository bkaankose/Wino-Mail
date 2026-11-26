using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Editor;
using Wino.Mail.Controls.Core.ContextFlyout;
using Wino.Renderer;
using Wino.Renderer.Editing;

namespace Wino.Mail.WinUI.Rendering;

public sealed partial class EngineMailEditorView
{
    private const int MaximumSpellingSuggestions = 5;

    /// <summary>
    /// The engine view shows no menu of its own: it reports what is under the pointer and the host builds the menu.
    /// </summary>
    private void View_ContextMenuRequested(object? sender, ContextMenuRequest request)
    {
        var entries = new List<ContextFlyoutMenuEntry>();

        if (request.IsEditable && request.MisspelledWord is not null)
        {
            foreach (var suggestion in request.SpellingSuggestions.Take(MaximumSpellingSuggestions))
                entries.Add(CreateEntry(suggestion, () => _view.ApplySpellingSuggestion(request, suggestion), "EditorContextSpellingSuggestion"));

            if (request.SpellingSuggestions.Count == 0)
                entries.Add(CreateEntry(Translator.Composer_SpellingNoSuggestions, static () => { }, "EditorContextNoSpellingSuggestions", isEnabled: false));

            entries.Add(CreateEntry(Translator.Composer_SpellingIgnore, () => _view.IgnoreSpelling(request), "EditorContextIgnoreSpelling"));
            entries.Add(CreateEntry(Translator.Composer_SpellingAddToDictionary, () => _view.AddToSpellingDictionary(request), "EditorContextAddToDictionary"));
            entries.Add(ContextFlyoutSeparatorEntry.Instance);
        }

        if (request.IsEditable)
        {
            entries.Add(CreateEntry(
                Translator.MacOSMenu_Cut,
                () => _view.Cut(),
                "EditorContextCut",
                request.HasSelection,
                new ContextFlyoutShortcut("Ctrl+X", "X", Control: true)));
        }

        entries.Add(CreateEntry(
            Translator.Buttons_Copy,
            () => _view.CopySelection(),
            "EditorContextCopy",
            request.HasSelection,
            new ContextFlyoutShortcut("Ctrl+C", "C", Control: true)));

        if (request.IsEditable)
        {
            entries.Add(CreateEntry(
                Translator.MacOSMenu_Paste,
                _view.Paste,
                "EditorContextPaste",
                shortcut: new ContextFlyoutShortcut("Ctrl+V", "V", Control: true)));
            entries.Add(CreateEntry(
                Translator.Composer_PasteAsPlainText,
                _view.PastePlainText,
                "EditorContextPastePlainText",
                shortcut: new ContextFlyoutShortcut("Ctrl+Shift+V", "V", Control: true, Shift: true)));
        }

        // A picture in the composer can be resized and described; the reader only copies it.
        if (request.IsEditable && request.OnImage)
        {
            entries.Add(ContextFlyoutSeparatorEntry.Instance);
            entries.Add(CreateEntry(
                Translator.Composer_ResizePicture,
                () => RunPictureCommand(Session.ResizeSelectedImageAsync),
                "EditorContextResizePicture",
                icon: WinoIconGlyph.ImageResize));
            entries.Add(CreateEntry(
                Translator.Composer_PictureDescription,
                () => RunPictureCommand(Session.DescribeSelectedImageAsync),
                "EditorContextPictureDescription",
                icon: WinoIconGlyph.TextDescription));
        }

        if (request.LinkUrl is { Length: > 0 } linkUrl)
        {
            entries.Add(ContextFlyoutSeparatorEntry.Instance);
            entries.Add(CreateEntry(Translator.Composer_OpenLink, () => OpenLink(linkUrl), "EditorContextOpenLink"));

            // Removing a link acts on the caret, so it is offered only when the caret is in a link.
            if (request.IsEditable && !string.IsNullOrWhiteSpace(_view.EditState?.LinkUrl))
                entries.Add(CreateEntry(Translator.Composer_RemoveLink, RemoveLink, "EditorContextRemoveLink"));
        }

        // A quoted message or a block with its own layout is one object until the user asks to edit inside it.
        if (_view.CanExecute(EditCommandKind.MakeIslandEditable))
        {
            entries.Add(ContextFlyoutSeparatorEntry.Instance);
            entries.Add(CreateEntry(
                Translator.Composer_EditProtectedContent,
                () => _view.Execute(EditCommandKind.MakeIslandEditable),
                "EditorContextEditProtectedContent"));
        }

        entries.Add(ContextFlyoutSeparatorEntry.Instance);
        entries.Add(CreateEntry(
            Translator.MacOSMenu_SelectAll,
            _view.SelectAll,
            "EditorContextSelectAll",
            shortcut: new ContextFlyoutShortcut("Ctrl+A", "A", Control: true)));

        EngineContextMenu.Show(_view, new Point(request.ViewPoint.X, request.ViewPoint.Y), entries, IsEditorDarkMode ? ElementTheme.Dark : ElementTheme.Light);
    }

    private ContextFlyoutCommandEntry CreateEntry(
        string text,
        Action action,
        string automationId,
        bool isEnabled = true,
        ContextFlyoutShortcut? shortcut = null,
        WinoIconGlyph? icon = null)
        => new()
        {
            Text = text,
            Icon = icon is { } glyph ? new ContextFlyoutIcon(WinoIconGlyphs.GetGlyph(glyph)) : null,
            Command = new RelayCommand(() =>
            {
                action();
                _view.Focus(FocusState.Programmatic);
            }),
            IsEnabled = isEnabled,
            Shortcut = shortcut,
            AutomationId = automationId
        };

    private static async void RunPictureCommand(Func<CancellationToken, Task> command)
    {
        try
        {
            await command(CancellationToken.None);
        }
        catch (ObjectDisposedException)
        {
            // The editor closed while its dialog was open.
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "A picture command from the composer menu failed.");
        }
    }

    private void OpenLink(string linkUrl)
    {
        if (Uri.TryCreate(linkUrl, UriKind.Absolute, out var uri))
            LinkOpenRequested?.Invoke(this, uri);
    }

    private async void RemoveLink()
    {
        try
        {
            await Session.ExecuteCommandAsync(EditorCommand.RemoveLink());
        }
        catch (ObjectDisposedException)
        {
            // The editor closed while its menu was open.
        }
    }
}
