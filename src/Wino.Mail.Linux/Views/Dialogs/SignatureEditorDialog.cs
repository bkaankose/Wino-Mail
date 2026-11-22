using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Dialogs;

/// <summary>
/// The signature editor (Mac <c>SignatureEditorSheet</c>, Windows <c>SignatureEditorDialog</c>): heading, the account line,
/// the name, the rich-text editor and the images hint; Save stays insensitive while the name is blank or the editor loads.
/// The result is the trimmed name and the editor HTML, or null when cancelled. Main thread.
/// </summary>
internal sealed class SignatureEditorDialog
{
    private readonly string? _accountLine;
    private readonly string? _name;
    private readonly string? _html;
    private readonly IDispatcher _dispatcher;
    private readonly IExternalLauncher? _launcher;
    private readonly IPreferencesService? _preferences;
    private readonly ITranslationService? _translations;
    private readonly Action<Exception> _error;

    public SignatureEditorDialog(string? accountLine, string? name, string? html, IDispatcher dispatcher, IExternalLauncher? launcher,
        IPreferencesService? preferences, ITranslationService? translations, Action<Exception> error)
    {
        _accountLine = accountLine;
        _name = name;
        _html = html;
        _dispatcher = dispatcher;
        _launcher = launcher;
        _preferences = preferences;
        _translations = translations;
        _error = error;
    }

#if DEBUG
    internal static SignatureEditorDialog.Session? DebugCurrent { get; private set; }
#endif

    public Task<(string Name, string HtmlBody)?> PresentAsync(Gtk.Widget parent)
    {
        var session = new Session(this);
#if DEBUG
        DebugCurrent = session;
#endif
        return session.PresentAsync(parent);
    }

    internal sealed class Session
    {
        private readonly TaskCompletionSource<(string Name, string HtmlBody)?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<IDisposable> _subscriptions = [];
        private readonly Adw.Dialog _dialog = Adw.Dialog.New();
        private readonly Adw.EntryRow _nameRow = Adw.EntryRow.New();
        private readonly Gtk.Button _save = Gtk.Button.NewWithLabel(Translator.Buttons_Save);
        private readonly Gtk.Button _cancel = Gtk.Button.NewWithLabel(Translator.Buttons_Cancel);
        private readonly WinoRichTextEditorView _editor;
        private bool _saving;
        private bool _finished;

        public Session(SignatureEditorDialog owner)
        {
            WinoLiveObjects.Track(this);
            _editor = new WinoRichTextEditorView(new WinoRichTextEditorOptions(), Translator.SignatureEditorDialog_Title, owner._dispatcher,
                owner._launcher, owner._preferences, owner._translations, owner._error);

            _dialog.SetTitle(Translator.SignatureEditorDialog_Title);
            _dialog.SetContentWidth(640);
            _dialog.SetContentHeight(560);
            _dialog.SetFollowsContentSize(false);

            var header = Adw.HeaderBar.New();
            header.SetShowEndTitleButtons(false);
            header.SetShowStartTitleButtons(false);
            header.SetTitleWidget(Adw.WindowTitle.New(Translator.SignatureEditorDialog_Title, owner._accountLine ?? string.Empty));
            WinoA11y.Rename(_cancel, Translator.Buttons_Cancel);
            WinoA11y.Rename(_save, Translator.Buttons_Save);
            _save.AddCssClass("suggested-action");
            header.PackStart(_cancel);
            header.PackEnd(_save);

            _nameRow.SetUseMarkup(false);
            _nameRow.SetTitle(Translator.SignatureEditorDialog_SignatureName_TitleNew);
            _nameRow.SetText(owner._name ?? string.Empty);
            var group = Adw.PreferencesGroup.New();
            group.Add(_nameRow);

            var hint = Gtk.Label.New(Translator.SignatureEditorDialog_ImagesHint);
            hint.AddCssClass("dim-label");
            hint.AddCssClass("caption");
            hint.SetXalign(0);
            hint.SetWrap(true);

            var content = Gtk.Box.New(Gtk.Orientation.Vertical, 12);
            content.SetMarginTop(6);
            content.SetMarginBottom(18);
            content.SetMarginStart(18);
            content.SetMarginEnd(18);
            content.Append(group);
            content.Append(_editor.Root);
            content.Append(hint);

            var toolbar = Adw.ToolbarView.New();
            toolbar.AddTopBar(header);
            toolbar.SetContent(content);
            _dialog.SetChild(toolbar);
            _dialog.SetFocus(_nameRow);

            _subscriptions.Add(GtkSignals.OnClicked(_cancel, () => Finish(null)));
            _subscriptions.Add(GtkSignals.OnClicked(_save, () => _ = SaveAsync()));
            _subscriptions.Add(GtkSignals.OnChanged(_nameRow, Refresh));
            _subscriptions.Add(GtkSignals.Connect<Adw.Dialog>(h => _dialog.OnCloseAttempt += h, h => _dialog.OnCloseAttempt -= h, (_, _) =>
            {
                if (!_saving) Finish(null);
            }));
            _subscriptions.Add(GtkSignals.Connect<Adw.Dialog>(h => _dialog.OnClosed += h, h => _dialog.OnClosed -= h, (_, _) => Closed()));
            EventHandler ready = (_, _) => Refresh();
            _editor.ReadyChanged += ready;
            _subscriptions.Add(new ActionDisposable(() => _editor.ReadyChanged -= ready));
            _dialog.SetCanClose(false);
            Refresh();
            _ = LoadAsync(owner._html);
        }

        public bool CanSave => !_saving && _editor.IsReady && !string.IsNullOrWhiteSpace(_nameRow.GetText());
        public WinoRichTextEditorView Editor => _editor;
        public Adw.EntryRow NameRow => _nameRow;
        public Gtk.Button SaveButton => _save;

        public Task<(string Name, string HtmlBody)?> PresentAsync(Gtk.Widget parent)
        {
            _dialog.Present(parent);
            return _result.Task;
        }

        public void Cancel() => Finish(null);

        public Task SaveAsync()
        {
            if (!CanSave) return Task.CompletedTask;
            return SaveCoreAsync();
        }

        private async Task SaveCoreAsync()
        {
            _saving = true;
            Refresh();
            try
            {
                var html = await _editor.GetHtmlBodyAsync();
                Finish((_nameRow.GetText().Trim(), html ?? string.Empty));
            }
            catch (Exception exception)
            {
                _saving = false;
                Refresh();
                Serilog.Log.Error(exception, "The signature could not be read from the editor.");
            }
        }

        private async Task LoadAsync(string? html)
        {
            try { await _editor.LoadAsync(html, Adw.StyleManager.GetDefault().Dark); }
            catch (Exception exception) { Serilog.Log.Error(exception, "The signature editor could not load."); }
        }

        private void Refresh() => _save.SetSensitive(CanSave);

        private void Finish((string Name, string HtmlBody)? result)
        {
            if (_finished) return;
            _finished = true;
            _result.TrySetResult(result);
            _dialog.ForceClose();
        }

        private async void Closed()
        {
            _finished = true;
            _result.TrySetResult(null);
            foreach (var subscription in _subscriptions) subscription.Dispose();
            _subscriptions.Clear();
#if DEBUG
            if (ReferenceEquals(DebugCurrent, this)) DebugCurrent = null;
#endif
            try { await _editor.DisposeAsync(); }
            catch (Exception exception) { Serilog.Log.Error(exception, "The signature editor could not be released."); }
        }
    }
}
