using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Wino.Core.Domain.Interfaces;
using Wino.Editor;
using Wino.Renderer.Editing;

namespace Wino.Mail.WinUI.Rendering;

internal sealed partial class EngineMailEditorSession
{
    private const string DefaultEmoji = "😊";

    public Task ExecuteCommandAsync(EditorCommand command) => ExecuteCommandAsync(command, default);

    public Task ExecuteCommandAsync(EditorCommand command, CancellationToken cancellationToken)
        => RunAsync(() => ExecuteCommandCoreAsync(command, cancellationToken));

    private async Task ExecuteCommandCoreAsync(EditorCommand command, CancellationToken cancellationToken)
    {
        if (_disposed || ApplySetting(command)) return;

        if (await WhenIdleAsync(cancellationToken) is null) return;

        if (command.Kind == EditorCommandKind.InsertImage)
            await PickAndInsertImagesAsync();
        else
            ExecuteEdit(command);

        // A toolbar button took the focus; typing continues in the editor.
        if (!_disposed)
            _view.Focus(FocusState.Programmatic);
    }

    /// <summary>The commands that change how the editor behaves, not its content. They need no loaded document.</summary>
    private bool ApplySetting(EditorCommand command)
    {
        switch (command.Kind)
        {
            case EditorCommandKind.ToggleTheme:
                ApplyTheme(command.Value is true);
                return true;
            case EditorCommandKind.ToggleSpellCheck:
                _isSpellCheckEnabled = command.Value is true;
                ApplySpelling();
                RaiseStateChanged();
                return true;
            // The saved choice may name a language Windows cannot check here: a preference from before the list came
            // from Windows, or one made on another device.
            case EditorCommandKind.SetSpellCheckLanguage:
                _spellCheckLanguage = EngineSpellCheckLanguages.Resolve(
                    command.Value as string,
                    EngineSpellCheckLanguages.GetInstalledCodes());
                ApplySpelling();
                return true;
            case EditorCommandKind.ToggleAutoCorrect:
                _isAutoCorrectEnabled = command.Value is true;
                ApplySpelling();
                return true;
            case EditorCommandKind.ToggleBuiltInToolbar:
                // The engine view has no toolbar of its own.
                return true;
            default:
                return false;
        }
    }

    private bool ExecuteEdit(EditorCommand command)
    {
        var state = CurrentState;

        switch (command.Kind)
        {
            case EditorCommandKind.ToggleBold: return _view.Execute(EditCommandKind.ToggleBold);
            case EditorCommandKind.ToggleItalic: return _view.Execute(EditCommandKind.ToggleItalic);
            case EditorCommandKind.ToggleUnderline: return _view.Execute(EditCommandKind.ToggleUnderline);
            case EditorCommandKind.ToggleStrikethrough: return _view.Execute(EditCommandKind.ToggleStrikethrough);
            case EditorCommandKind.ToggleOrderedList: return _view.Execute(EditCommandKind.ToggleOrderedList);
            case EditorCommandKind.ToggleUnorderedList: return _view.Execute(EditCommandKind.ToggleUnorderedList);
            case EditorCommandKind.Indent: return _view.Execute(EditCommandKind.Indent);
            case EditorCommandKind.Outdent: return _view.Execute(EditCommandKind.Outdent);
            case EditorCommandKind.ClearFormatting: return _view.Execute(EditCommandKind.ClearFormatting);
            case EditorCommandKind.Undo: return _view.Undo();
            case EditorCommandKind.Redo: return _view.Redo();

            case EditorCommandKind.SetAlignment:
                var alignment = command.Value is EditorTextAlignment value ? value : EditorTextAlignment.Left;
                return _view.Execute(EditCommandKind.SetAlignment, new AlignmentArgs(EngineEditorMapping.ToEngineAlignment(alignment)));

            case EditorCommandKind.SetFontFamily when command.Value is string { Length: > 0 } fontFamily:
                return _view.Execute(EditCommandKind.SetFontFamily, new FontFamilyArgs(fontFamily));

            // Both sides count the font size in CSS pixels.
            case EditorCommandKind.SetFontSize when command.Value is int fontSize:
                return _view.Execute(EditCommandKind.SetFontSize, new FontSizeArgs(Math.Clamp(fontSize, 8, 72)));

            case EditorCommandKind.SetParagraphStyle:
                return _view.Execute(
                    EditCommandKind.SetParagraphStyle,
                    new ParagraphStyleArgs(EngineEditorMapping.ToParagraphStyle(command.Value as string)));

            case EditorCommandKind.SetTextColor:
                return _view.Execute(EditCommandKind.SetTextColor, new ColorArgs(EngineEditorMapping.ToColor(command.Value as string)));

            case EditorCommandKind.SetHighlightColor:
                return _view.Execute(EditCommandKind.SetHighlightColor, new ColorArgs(EngineEditorMapping.ToColor(command.Value as string)));

            case EditorCommandKind.SetLineHeight:
                var lineHeight = command.Value is string { Length: > 0 } height ? height : "normal";
                return _view.Execute(EditCommandKind.SetLineHeight, new LineHeightArgs(lineHeight));

            case EditorCommandKind.SetImageProperties when command.Value is EditorImagePropertiesCommandArgs image:
                return _view.Execute(
                    EditCommandKind.SetImageProperties,
                    new ImagePropertiesArgs(image.AltText, NullIfBlank(image.LinkUrl), image.OpenInNewWindow));

            // A selected image takes the link itself and keeps its alternative text.
            case EditorCommandKind.InsertLink when command.Value is EditorLinkCommandArgs link && !string.IsNullOrWhiteSpace(link.Url):
                var url = EditorLinkRules.NormalizeUrl(link.Url);
                return state.IsImageSelected
                    ? _view.Execute(
                        EditCommandKind.SetImageProperties,
                        new ImagePropertiesArgs(state.ImageAltText ?? string.Empty, url, link.OpenInNewWindow))
                    : _view.Execute(EditCommandKind.InsertLink, new LinkArgs(url, NullIfBlank(link.Text), link.OpenInNewWindow));

            case EditorCommandKind.RemoveLink:
                return state.IsImageSelected
                    ? _view.Execute(EditCommandKind.SetImageProperties, new ImagePropertiesArgs(state.ImageAltText ?? string.Empty, null))
                    : _view.Execute(EditCommandKind.RemoveLink);

            case EditorCommandKind.InsertEmoji:
                var emoji = command.Value is string { Length: > 0 } text ? text : DefaultEmoji;
                return _view.Execute(EditCommandKind.InsertText, new TextArgs(emoji));

            case EditorCommandKind.InsertTable when command.Value is EditorTableCommandArgs table:
                return _view.Execute(EditCommandKind.InsertTable, new TableArgs(Math.Clamp(table.Rows, 1, 50), Math.Clamp(table.Columns, 1, 20)));

            default:
                return false;
        }
    }

    private async Task PickAndInsertImagesAsync()
    {
        var dialogService = WinoApplication.Current.Services.GetRequiredService<IMailDialogService>();
        var files = await dialogService.PickFilesAsync(".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp");

        foreach (var file in files)
        {
            var args = await EngineEditorImages.CreateAsync(file.Data, file.FileName);
            if (_disposed || !_view.IsEditing) return;

            if (args is not null)
                _view.InsertImages(new[] { args });
        }
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
