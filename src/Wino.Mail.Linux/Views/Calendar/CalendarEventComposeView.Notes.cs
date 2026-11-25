using Wino.Core.Domain;
using Wino.Mail.Linux.Views.Dialogs;
using Wino.Mail.ViewModels.Calendar;

namespace Wino.Mail.Linux.Views.Calendar;

/// <summary>
/// The notes (Windows WinoMailEditor, macOS WinoRichTextEditorView): the shared rich-text editor; notes that were not
/// touched are saved exactly as they arrived (<see cref="CalendarNotesHtmlResolver"/>). An editor that cannot load
/// shows the notes as read-only text and saves the original HTML unchanged.
/// </summary>
internal sealed partial class CalendarEventComposeView
{
    private const int NotesHeight = 300;

    private readonly Gtk.Box _notesBox = Gtk.Box.New(Gtk.Orientation.Vertical, 6);
    private readonly Gtk.Overlay _notesOverlay = Gtk.Overlay.New();
    private readonly Adw.Spinner _notesSpinner = Adw.Spinner.New();
    private WinoRichTextEditorView? _notesEditor;
    private string _originalNotesHtml = string.Empty;
    private string? _baselineNotesBody;
    private bool _notesReady;
    private bool _notesEdited;
    private bool _notesFailed;

    private Gtk.Widget BuildNotes()
    {
        var label = Gtk.Label.New(Translator.CalendarEventCompose_Notes);
        label.AddCssClass("caption-heading");
        label.AddCssClass("dim-label");
        label.SetXalign(0);
        _notesSpinner.SetHalign(Gtk.Align.Center);
        _notesSpinner.SetValign(Gtk.Align.Center);
        _notesSpinner.SetSizeRequest(24, 24);
        _notesSpinner.SetCanTarget(false);
        _notesOverlay.SetSizeRequest(-1, NotesHeight);
        _notesOverlay.AddOverlay(_notesSpinner);
        _notesBox.Append(label);
        _notesBox.Append(_notesOverlay);
        return _notesBox;
    }

    private void StartNotes()
    {
        var editor = _notesEditor = new WinoRichTextEditorView(new WinoRichTextEditorOptions(Highlight: true, Table: true, Emoji: true),
            Translator.CalendarEventCompose_Notes, Dispatcher, _launcher, _preferences, _translations, ReportError);
        editor.ContentChanged += NotesContentChanged;
        _notesOverlay.SetChild(editor.Root);
        Observe(LoadNotesAsync(editor));
    }

    private async Task LoadNotesAsync(WinoRichTextEditorView editor)
    {
        try
        {
            await editor.LoadAsync(_originalNotesHtml, Adw.StyleManager.GetDefault().GetDark());
            string? baseline = null;
            try { baseline = await editor.GetHtmlBodyAsync(); }
            catch (Exception exception) { ReportError(exception); }
            await Dispatcher.ExecuteOnUIThread(() =>
            {
                if (IsReleased) return;
                _baselineNotesBody = baseline;
                _notesReady = true;
                _notesSpinner.SetVisible(false);
            });
        }
        catch (Exception exception)
        {
            ReportError(exception);
            await Dispatcher.ExecuteOnUIThread(ShowNotesFallback);
        }
    }

    private void NotesContentChanged(object? sender, EventArgs args)
    {
        if (_notesReady) _notesEdited = true;
    }

    /// <summary>Called by the ViewModel from a pool thread when it saves.</summary>
    private async Task<string> GetNotesHtmlAsync()
    {
        string? current = null;
        var ready = false;
        var failed = false;
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            ready = _notesReady;
            failed = _notesFailed;
        });
        if (!ready || failed || _notesEditor is not { } editor) return _originalNotesHtml;
        try
        {
            var read = Task.FromResult<string?>(null);
            await Dispatcher.ExecuteOnUIThread(() => read = editor.GetHtmlBodyAsync());
            current = await read;
        }
        catch (Exception exception) { ReportError(exception); }
        return CalendarNotesHtmlResolver.Resolve(_originalNotesHtml, _baselineNotesBody, current, ready, _notesEdited);
    }

    /// <summary>The editor failed to load: the notes as read-only text, saved unchanged.</summary>
    private void ShowNotesFallback()
    {
        if (IsReleased) return;
        _notesFailed = true;
        _notesSpinner.SetVisible(false);
        var text = Gtk.TextView.New();
        text.SetEditable(false);
        text.SetWrapMode(Gtk.WrapMode.WordChar);
        text.GetBuffer().SetText(StripHtml(_originalNotesHtml), -1);
        var scroller = Gtk.ScrolledWindow.New();
        scroller.AddCssClass("card");
        scroller.SetChild(text);
        _notesOverlay.SetChild(scroller);
    }

    private static string StripHtml(string html)
    {
        var text = HtmlBreaks().Replace(html, "\n");
        text = HtmlTags().Replace(text, string.Empty);
        return System.Net.WebUtility.HtmlDecode(text).Trim();
    }

    [System.Text.RegularExpressions.GeneratedRegex("<br\\s*/?>|</p>|</div>", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex HtmlBreaks();

    [System.Text.RegularExpressions.GeneratedRegex("<[^>]+>")]
    private static partial System.Text.RegularExpressions.Regex HtmlTags();

    private async Task DisposeNotesAsync()
    {
        if (_notesEditor is not { } editor) return;
        _notesEditor = null;
        editor.ContentChanged -= NotesContentChanged;
        try { await editor.DisposeAsync(); }
        catch (Exception exception) { ReportError(exception); }
    }

#if DEBUG
    internal string NotesMode => _notesFailed ? "readonly" : _notesReady ? "editor" : "loading";

    internal async Task DebugSetNotesAsync(string text)
    {
        if (_notesEditor is not { } editor) return;
        await editor.SetHtmlAsync($"<p>{System.Net.WebUtility.HtmlEncode(text)}</p>");
        _notesEdited = true;
    }
#endif
}
