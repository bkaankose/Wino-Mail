using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Models.Common;
using Wino.Editor;

namespace Wino.Mail.UIKit.Views.Mail.Compose;

/// <summary>
/// The editor toolbar dialogs (Mac EditorDialogs, Windows EditorTabbedCommandBarControl): link, image properties and
/// table size as alerts with text fields, plus turning picked pictures into inline data URIs. iOS alerts have no
/// check boxes, so links keep the Windows default of opening in a new window. Main thread only.
/// </summary>
internal static class EditorDialogs
{
    internal const int TableMinimum = 1;
    internal const int TableMaximum = 10;
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".webp", ".heic", ".bmp"];

    /// <summary>The picker filters for inline pictures.</summary>
    internal static readonly object[] ImageFilters = ["png", "jpg", "jpeg", "gif", "webp", "heic", "bmp"];

    /// <summary>
    /// Insert or edit a link. With an image selected it edits the image link and has no text field (Windows
    /// ShowLinkDialogAsync). Null when cancelled or the URL is empty.
    /// </summary>
    public static async Task<EditorLinkCommandArgs?> LinkAsync(UIViewController presenter, EditorState state)
    {
        var title = EditorLinkRules.Kind(state) switch
        {
            EditorLinkDialogKind.EditImageLink => Translator.Composer_EditImageLink,
            EditorLinkDialogKind.EditLink => Translator.ComposerParity_EditLink,
            _ => Translator.Composer_InsertLink
        };
        var fields = new List<FieldSpec> { new(Translator.Composer_LinkUrlPlaceholder, EditorLinkRules.InitialUrl(state), UIKeyboardType.Url) };
        if (EditorLinkRules.HasTextField(state)) fields.Add(new(Translator.Composer_LinkTextPlaceholder, state.SelectedText, UIKeyboardType.Default));
        var values = await PromptAsync(presenter, title, null, fields, Translator.Buttons_Apply);
        if (values is null) return null;
        return EditorLinkRules.CreateLink(state, values[0], values.Length < 2 ? null : values[1]);
    }

    /// <summary>Alternative text and link of the selected image (Windows ShowImagePropertiesDialogAsync).</summary>
    public static async Task<EditorImagePropertiesCommandArgs?> ImagePropertiesAsync(UIViewController presenter, EditorState state)
    {
        if (!state.IsImageSelected) return null;
        var values = await PromptAsync(presenter, Translator.Composer_ImageProperties, null,
        [
            new(Translator.Composer_ImageAltTextPlaceholder, state.ImageAltText, UIKeyboardType.Default),
            new(Translator.Composer_LinkUrlPlaceholder, state.ImageLinkUrl, UIKeyboardType.Url)
        ], Translator.Buttons_Apply);
        if (values is null) return null;
        return new EditorImagePropertiesCommandArgs(values[0].Trim(), string.IsNullOrWhiteSpace(values[1]) ? null : NormalizeUrl(values[1]), true);
    }

    /// <summary>Rows and columns, 1–10 each, 2×2 by default (Windows TableButton_Click).</summary>
    public static async Task<EditorTableCommandArgs?> TableAsync(UIViewController presenter)
    {
        var values = await PromptAsync(presenter, Translator.Composer_InsertTable, null,
        [
            new(Translator.Composer_TableRows, "2", UIKeyboardType.NumberPad),
            new(Translator.Composer_TableColumns, "2", UIKeyboardType.NumberPad)
        ], Translator.Buttons_Insert);
        if (values is null) return null;
        return new EditorTableCommandArgs(ParseTable(values[0]), ParseTable(values[1]));
    }

    internal static int ClampTable(int value) => Math.Clamp(value, TableMinimum, TableMaximum);

    private static int ParseTable(string value) => ClampTable(int.TryParse(value?.Trim(), out var parsed) ? parsed : 2);

    internal static string NormalizeUrl(string value) => EditorLinkRules.NormalizeUrl(value);

    internal static bool IsImage(string path) => ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>Picked or dropped pictures as data URIs, like the composer's inline images.</summary>
    public static List<EditorImageInfo> ToInlineImages(IEnumerable<SharedFile> files)
    {
        var images = new List<EditorImageInfo>();
        foreach (var file in files)
        {
            if (file.Data is not { Length: > 0 } data || string.IsNullOrWhiteSpace(file.FullFilePath) || !IsImage(file.FullFilePath)) continue;
            images.Add(new EditorImageInfo($"data:{MimeType(file.FullFilePath)};base64,{Convert.ToBase64String(data)}", Path.GetFileName(file.FullFilePath)));
        }
        return images;
    }

    private static string MimeType(string path) => Path.GetExtension(path).TrimStart('.').ToLowerInvariant() switch
    {
        "jpg" or "jpeg" => "image/jpeg",
        "gif" => "image/gif",
        "webp" => "image/webp",
        "heic" => "image/heic",
        "bmp" => "image/bmp",
        _ => "image/png"
    };

    private sealed record FieldSpec(string Placeholder, string? Value, UIKeyboardType Keyboard);

    /// <summary>An alert with one text field per spec; the values in order, or null when cancelled.</summary>
    private static Task<string[]?> PromptAsync(UIViewController presenter, string title, string? message, IReadOnlyList<FieldSpec> fields, string primary)
    {
        var completion = new TaskCompletionSource<string[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var alert = UIAlertController.Create(title, message, UIAlertControllerStyle.Alert);
        foreach (var spec in fields)
        {
            alert.AddTextField(field =>
            {
                field.Placeholder = spec.Placeholder;
                field.Text = spec.Value ?? string.Empty;
                field.KeyboardType = spec.Keyboard;
                field.ClearButtonMode = UITextFieldViewMode.WhileEditing;
                if (spec.Keyboard == UIKeyboardType.Url)
                {
                    field.AutocapitalizationType = UITextAutocapitalizationType.None;
                    field.AutocorrectionType = UITextAutocorrectionType.No;
                }
                field.AccessibilityLabel = spec.Placeholder;
            });
        }
        alert.AddAction(UIAlertAction.Create(Translator.Buttons_Cancel, UIAlertActionStyle.Cancel, _ => completion.TrySetResult(null)));
        var apply = UIAlertAction.Create(primary, UIAlertActionStyle.Default, _ =>
        {
            var values = (alert.TextFields ?? []).Select(static field => field.Text ?? string.Empty).ToArray();
            completion.TrySetResult(values.Length == fields.Count ? values : null);
        });
        alert.AddAction(apply);
        alert.PreferredAction = apply;
        presenter.PresentViewController(alert, true, null);
        return completion.Task;
    }
}
