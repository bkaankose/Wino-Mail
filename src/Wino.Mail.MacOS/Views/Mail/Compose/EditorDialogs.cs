using AppKit;
using CoreGraphics;
using Wino.Core.Domain;
using Wino.Editor;

namespace Wino.Mail.MacOS.Views.Mail.Compose;

/// <summary>
/// Sheets shared by every HTML editor toolbar (Windows EditorTabbedCommandBarControl dialogs): link,
/// image properties and table size, plus the picture picker. All run on the main thread.
/// </summary>
internal static class EditorDialogs
{
    private const double FormWidth = 320;
    private const double LabelWidth = 96;
    private const double RowHeight = 24;
    private const double RowGap = 8;
    internal const int TableMinimum = 1;
    internal const int TableMaximum = 10;

    /// <summary>
    /// Insert or edit a link. With an image selected the sheet edits the image link and hides the text
    /// field (Windows ShowLinkDialogAsync). Returns null when cancelled or the URL is empty.
    /// </summary>
    public static async Task<EditorLinkCommandArgs?> LinkAsync(NSWindow window, EditorState state)
    {
        var isImage = state.IsImageSelected;
        var url = Field(Translator.Composer_LinkUrlPlaceholder, isImage ? state.ImageLinkUrl : state.LinkUrl);
        var text = Field(Translator.Composer_LinkTextPlaceholder, state.SelectedText);
        var openInNewWindow = CheckBox(Translator.Composer_OpenLinkInNewWindow, true);
        var rows = new List<(string?, NSView)> { (Translator.Composer_LinkUrl, url) };
        if (!isImage) rows.Add((Translator.Composer_LinkText, text));
        rows.Add((null, openInNewWindow));

        var title = isImage
            ? Translator.Composer_EditImageLink
            : string.IsNullOrWhiteSpace(state.LinkUrl) ? Translator.Composer_InsertLink : Translator.ComposerParity_EditLink;
        var alert = Alert(title, Form(rows), Translator.Buttons_Apply);
        alert.Window.InitialFirstResponder = url;
        if (!await RunAsync(alert, window) || string.IsNullOrWhiteSpace(url.StringValue)) return null;

        var text1 = isImage || string.IsNullOrWhiteSpace(text.StringValue) ? null : text.StringValue.Trim();
        return new EditorLinkCommandArgs(NormalizeUrl(url.StringValue), text1, openInNewWindow.State == NSCellStateValue.On);
    }

    /// <summary>Alternative text and link of the selected image (Windows ShowImagePropertiesDialogAsync).</summary>
    public static async Task<EditorImagePropertiesCommandArgs?> ImagePropertiesAsync(NSWindow window, EditorState state)
    {
        if (!state.IsImageSelected) return null;
        var alt = Field(Translator.Composer_ImageAltTextPlaceholder, state.ImageAltText);
        var url = Field(Translator.Composer_LinkUrlPlaceholder, state.ImageLinkUrl);
        var openInNewWindow = CheckBox(Translator.Composer_OpenLinkInNewWindow, true);
        var alert = Alert(Translator.Composer_ImageProperties,
            Form([(Translator.Composer_ImageAltText, alt), (Translator.Composer_LinkUrl, url), (null, openInNewWindow)]), Translator.Buttons_Apply);
        alert.Window.InitialFirstResponder = alt;
        if (!await RunAsync(alert, window)) return null;
        return new EditorImagePropertiesCommandArgs(alt.StringValue.Trim(),
            string.IsNullOrWhiteSpace(url.StringValue) ? null : NormalizeUrl(url.StringValue),
            openInNewWindow.State == NSCellStateValue.On);
    }

    /// <summary>Rows and columns, 1–10 each, 2×2 by default (Windows TableButton_Click).</summary>
    public static async Task<EditorTableCommandArgs?> TableAsync(NSWindow window)
    {
        var (rowsView, rowsField) = Stepper(2);
        var (columnsView, columnsField) = Stepper(2);
        var alert = Alert(Translator.Composer_InsertTable,
            Form([(Translator.Composer_TableRows, rowsView), (Translator.Composer_TableColumns, columnsView)]), Translator.Buttons_Insert);
        alert.Window.InitialFirstResponder = rowsField;
        if (!await RunAsync(alert, window)) return null;
        return new EditorTableCommandArgs(ClampTable(rowsField.IntValue), ClampTable(columnsField.IntValue));
    }

    internal static int ClampTable(int value) => Math.Clamp(value, TableMinimum, TableMaximum);

    /// <summary>Picks pictures and turns them into data URIs, like the composer's inline images.</summary>
    public static async Task<List<EditorImageInfo>> PickImagesAsync()
    {
        var images = new List<EditorImageInfo>();
        var panel = NSOpenPanel.OpenPanel;
        panel.AllowsMultipleSelection = true;
        panel.CanChooseDirectories = false;
#pragma warning disable CA1422
        panel.AllowedFileTypes = ["png", "jpg", "jpeg", "gif", "webp", "heic", "bmp"];
#pragma warning restore CA1422
        if (panel.RunModal() != 1) return images;
        foreach (var url in panel.Urls)
        {
            if (url.Path is not { } path) continue;
            var bytes = await File.ReadAllBytesAsync(path);
            var extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            var mime = extension switch { "jpg" or "jpeg" => "image/jpeg", "gif" => "image/gif", "webp" => "image/webp", "heic" => "image/heic", "bmp" => "image/bmp", _ => "image/png" };
            images.Add(new EditorImageInfo($"data:{mime};base64,{Convert.ToBase64String(bytes)}", Path.GetFileName(path)));
        }
        return images;
    }

    internal static string NormalizeUrl(string value)
    {
        var address = value.Trim();
        if (!address.Contains("://", StringComparison.Ordinal) && !address.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) address = "https://" + address;
        return address;
    }

    // ---- Building blocks ----

    private static NSAlert Alert(string title, NSView accessory, string primary)
    {
        var alert = new NSAlert { MessageText = title, AccessoryView = accessory };
        alert.AddButton(primary);
        var cancel = alert.AddButton(Translator.Buttons_Cancel);
        cancel.KeyEquivalent = "\u001b";
        return alert;
    }

    private static async Task<bool> RunAsync(NSAlert alert, NSWindow window)
        => (long)await alert.BeginSheetAsync(window) == (long)NSAlertButtonReturn.First;

    private static NSTextField Field(string placeholder, string? value)
        => new() { PlaceholderString = placeholder, StringValue = value ?? string.Empty, UsesSingleLineMode = true };

    private static NSButton CheckBox(string title, bool on)
    {
        var box = NSButton.CreateCheckbox(title, () => { });
        box.State = on ? NSCellStateValue.On : NSCellStateValue.Off;
        return box;
    }

    private static (NSView View, NSTextField Field) Stepper(int value)
    {
        var field = new NSTextField { IntValue = value, Alignment = NSTextAlignment.Right };
        field.Formatter = new Foundation.NSNumberFormatter { Minimum = TableMinimum, Maximum = TableMaximum, AllowsFloats = false };
        var stepper = new NSStepper { MinValue = TableMinimum, MaxValue = TableMaximum, Increment = 1, IntValue = value, ValueWraps = false };
        stepper.Activated += (_, _) => field.IntValue = stepper.IntValue;
        field.Changed += (_, _) => stepper.IntValue = ClampTable(field.IntValue);
        var host = new NSView(new CGRect(0, 0, FormWidth - LabelWidth, RowHeight));
        field.Frame = new CGRect(0, 0, 56, RowHeight);
        stepper.Frame = new CGRect(60, 0, 19, RowHeight);
        host.AddSubview(field);
        host.AddSubview(stepper);
        return (host, field);
    }

    /// <summary>A label column and a field column; a null label lets the control span both.</summary>
    private static NSView Form(IReadOnlyList<(string? Label, NSView Control)> rows)
    {
        var height = rows.Count * RowHeight + (rows.Count - 1) * RowGap;
        var form = new NSView(new CGRect(0, 0, FormWidth, height));
        for (int index = 0; index < rows.Count; index++)
        {
            var y = height - (index + 1) * RowHeight - index * RowGap;
            var (label, control) = rows[index];
            control.TranslatesAutoresizingMaskIntoConstraints = true;
            if (label is null)
            {
                control.Frame = new CGRect(LabelWidth + 8, y, FormWidth - LabelWidth - 8, RowHeight);
            }
            else
            {
                var caption = NSTextField.CreateLabel(label);
                caption.Alignment = NSTextAlignment.Right;
                caption.Frame = new CGRect(0, y + 3, LabelWidth, RowHeight - 6);
                form.AddSubview(caption);
                control.Frame = new CGRect(LabelWidth + 8, y, FormWidth - LabelWidth - 8, RowHeight);
            }
            form.AddSubview(control);
        }
        return form;
    }
}
