using Wino.Core.Domain;
using Wino.Editor;
using Wino.Mail.Linux.Views.Dialogs;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Mail.Compose;

/// <summary>
/// Forms shared by every editor toolbar (Mac <c>EditorDialogs</c>, Windows EditorLinkDialog, EditorImagePropertiesDialog and
/// the table flyout): link, image properties and table size, plus the picture picker. Main thread only.
/// </summary>
internal static class EditorDialogs
{
    internal const int TableMinimum = 1;
    internal const int TableMaximum = 10;
    internal static readonly string[] ImageExtensions = ["png", "jpg", "jpeg", "gif", "webp", "bmp"];

    /// <summary>
    /// Insert or edit a link. With an image selected the form edits the image link and has no text field (Windows
    /// ShowLinkDialogAsync). Null when cancelled; Apply stays insensitive while the address is blank.
    /// </summary>
    public static async Task<EditorLinkCommandArgs?> LinkAsync(Gtk.Widget owner, EditorState state)
    {
        var title = EditorLinkRules.Kind(state) switch
        {
            EditorLinkDialogKind.EditImageLink => Translator.Composer_EditImageLink,
            EditorLinkDialogKind.EditLink => Translator.ComposerParity_EditLink,
            _ => Translator.Composer_InsertLink
        };
        var form = new FormDialog(title, null, Translator.Buttons_Apply);
        var group = form.AddGroup();
        var url = Entry(group, Translator.Composer_LinkUrl, EditorLinkRules.InitialUrl(state));
        var text = EditorLinkRules.HasTextField(state) ? Entry(group, Translator.Composer_LinkText, state.SelectedText) : null;
        var newWindow = Switch(group, Translator.Composer_OpenLinkInNewWindow, true);
        form.CanConfirm = () => !string.IsNullOrWhiteSpace(url.GetText());
        form.Own(GtkSignals.OnChanged(url, form.Refresh));
        form.SetInitialFocus(url);
        Remember(form);
        if (!await form.PresentAsync(owner)) return null;
        return EditorLinkRules.CreateLink(state, url.GetText(), text?.GetText(), newWindow.GetActive());
    }

    /// <summary>Alternative text and link of the selected image (Windows ShowImagePropertiesDialogAsync).</summary>
    public static async Task<EditorImagePropertiesCommandArgs?> ImagePropertiesAsync(Gtk.Widget owner, EditorState state)
    {
        if (!state.IsImageSelected) return null;
        var form = new FormDialog(Translator.Composer_ImageProperties, null, Translator.Buttons_Apply);
        var group = form.AddGroup();
        var alt = Entry(group, Translator.Composer_ImageAltText, state.ImageAltText);
        var url = Entry(group, Translator.Composer_LinkUrl, state.ImageLinkUrl);
        var newWindow = Switch(group, Translator.Composer_OpenLinkInNewWindow, true);
        form.SetInitialFocus(alt);
        Remember(form);
        if (!await form.PresentAsync(owner)) return null;
        var link = url.GetText();
        return new EditorImagePropertiesCommandArgs(alt.GetText().Trim(),
            string.IsNullOrWhiteSpace(link) ? null : EditorLinkRules.NormalizeUrl(link), newWindow.GetActive());
    }

    /// <summary>Rows and columns, 1–10 each, 2×2 by default (Mac, Windows TableButton_Click).</summary>
    public static async Task<EditorTableCommandArgs?> TableAsync(Gtk.Widget owner)
    {
        var form = new FormDialog(Translator.Composer_InsertTable, null, Translator.Buttons_Insert);
        var group = form.AddGroup();
        var rows = Spin(group, Translator.Composer_TableRows, 2);
        var columns = Spin(group, Translator.Composer_TableColumns, 2);
        form.SetInitialFocus(rows);
        Remember(form);
        if (!await form.PresentAsync(owner)) return null;
        rows.Update();
        columns.Update();
        return new EditorTableCommandArgs(ClampTable(rows.GetValueAsInt()), ClampTable(columns.GetValueAsInt()));
    }

    internal static int ClampTable(int value) => Math.Clamp(value, TableMinimum, TableMaximum);

    /// <summary>Picks pictures and reads them as inline data URIs. HEIC is left out: WebKitGTK cannot show it.</summary>
    public static async Task<EditorImageInfo[]> PickImagesAsync(Gtk.Window owner)
    {
        var paths = await FilePickers.OpenAsync(owner, true, ImageExtensions);
        var images = new List<EditorImageInfo>();
        foreach (var path in paths)
        {
            if (await ImageFromFileAsync(path) is { } image) images.Add(image);
        }
        return [.. images];
    }

    /// <summary>The file as an inline image, read off the main thread; null when it cannot be read.</summary>
    public static async Task<EditorImageInfo?> ImageFromFileAsync(string path)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            return new EditorImageInfo($"data:{MimeFor(Path.GetExtension(path))};base64,{Convert.ToBase64String(bytes)}", Path.GetFileName(path));
        }
        catch (IOException exception)
        {
            Serilog.Log.Warning(exception, "An inline picture could not be read.");
            return null;
        }
        catch (UnauthorizedAccessException exception)
        {
            Serilog.Log.Warning(exception, "An inline picture could not be read.");
            return null;
        }
    }

    internal static string MimeFor(string extension) => extension.TrimStart('.').ToLowerInvariant() switch
    {
        "jpg" or "jpeg" => "image/jpeg",
        "gif" => "image/gif",
        "webp" => "image/webp",
        "bmp" => "image/bmp",
        _ => "image/png"
    };

    internal static bool IsImageFile(string path)
        => ImageExtensions.Contains(Path.GetExtension(path).TrimStart('.').ToLowerInvariant(), StringComparer.Ordinal);

#if DEBUG
    private static WeakReference<FormDialog>? _current;

    /// <summary>The form shown last; Debug checks fill and confirm it.</summary>
    internal static FormDialog? Current => _current is { } weak && weak.TryGetTarget(out var form) ? form : null;
#endif

    private static void Remember(FormDialog form)
    {
#if DEBUG
        _current = new WeakReference<FormDialog>(form);
#endif
    }

    private static Adw.EntryRow Entry(Adw.PreferencesGroup group, string title, string? value)
    {
        var row = Adw.EntryRow.New();
        row.SetUseMarkup(false);
        row.SetTitle(title);
        row.SetText(value ?? string.Empty);
        group.Add(row);
        return row;
    }

    private static Adw.SwitchRow Switch(Adw.PreferencesGroup group, string title, bool active)
    {
        var row = Adw.SwitchRow.New();
        row.SetUseMarkup(false);
        row.SetTitle(title);
        row.SetActive(active);
        group.Add(row);
        return row;
    }

    // Adw.SpinRow is missing from the GTK 4.22 accessibility tree, so the number sits in a spin button suffix.
    private static Gtk.SpinButton Spin(Adw.PreferencesGroup group, string title, int value)
    {
        var row = Adw.ActionRow.New();
        row.SetUseMarkup(false);
        row.SetTitle(title);
        var spin = Gtk.SpinButton.NewWithRange(TableMinimum, TableMaximum, 1);
        spin.SetValue(value);
        spin.SetNumeric(true);
        spin.SetValign(Gtk.Align.Center);
        WinoA11y.Label(spin, title);
        row.AddSuffix(spin);
        row.SetActivatableWidget(spin);
        group.Add(row);
        return spin;
    }
}
