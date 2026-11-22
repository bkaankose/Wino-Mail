using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Editor.WebKitGtk;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Mail.Compose;

/// <summary>
/// The composer's find and replace bar (Mac <c>ComposeFindBar</c>): search entry, match counter, previous and next, an
/// options menu (Ignore Case, Whole Words, Replace…) and Done, with a replace row under it. It only raises requests; the
/// composer runs them on the editor.
/// </summary>
internal sealed class ComposeFindBar : IDisposable
{
    private readonly Gtk.Revealer _root = Gtk.Revealer.New();
    private readonly Gtk.SearchEntry _query = Gtk.SearchEntry.New();
    private readonly Gtk.Label _counter = Gtk.Label.New(string.Empty);
    private readonly Gtk.Button _previous = WinoIcons.Button(WinoIconGlyph.ChevronUp, Translator.Composer_FindPrevious);
    private readonly Gtk.Button _next = WinoIcons.Button(WinoIconGlyph.ChevronDown, Translator.Composer_FindNext);
    private readonly Gtk.Button _options = WinoIcons.Button(WinoIconGlyph.More, Translator.Composer_AttachmentMoreOptions);
    private readonly Gtk.Button _done = Gtk.Button.NewWithLabel(Translator.Buttons_Done);
    private readonly Gtk.Revealer _replaceRow = Gtk.Revealer.New();
    private readonly Gtk.Entry _replacement = Gtk.Entry.New();
    private readonly Gtk.Button _replace = Gtk.Button.NewWithLabel(Translator.Composer_Replace);
    private readonly Gtk.Button _replaceAll = Gtk.Button.NewWithLabel(Translator.Composer_ReplaceAll);
    private readonly List<IDisposable> _subscriptions = [];
    private bool _applying;

    public ComposeFindBar()
    {
        WinoLiveObjects.Track(this);
        var row = Gtk.Box.New(Gtk.Orientation.Horizontal, 4);
        _query.SetHexpand(true);
        _query.SetPlaceholderText(Translator.MailOperation_Find);
        WinoA11y.Label(_query, Translator.MailOperation_Find);
        _counter.AddCssClass("dim-label");
        _counter.AddCssClass("caption");
        _counter.AccessibleRole = Gtk.AccessibleRole.Status;
        _done.AddCssClass("flat");
        foreach (var widget in new Gtk.Widget[] { _counter, _previous, _next, _options, _done }) widget.SetHexpand(false);
        row.Append(_query);
        row.Append(_counter);
        row.Append(_previous);
        row.Append(_next);
        row.Append(_options);
        row.Append(_done);

        var replaceRow = Gtk.Box.New(Gtk.Orientation.Horizontal, 4);
        _replacement.SetHexpand(true);
        _replacement.SetPlaceholderText(Translator.Composer_Replace);
        WinoA11y.Label(_replacement, Translator.Composer_Replace);
        replaceRow.Append(_replacement);
        replaceRow.Append(_replace);
        replaceRow.Append(_replaceAll);
        replaceRow.SetMarginTop(4);
        _replaceRow.SetChild(replaceRow);
        _replaceRow.SetRevealChild(false);

        var content = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        content.AddCssClass("wino-compose-find");
        content.SetMarginStart(12);
        content.SetMarginEnd(12);
        content.SetMarginTop(4);
        content.SetMarginBottom(4);
        content.Append(row);
        content.Append(_replaceRow);
        content.AccessibleRole = Gtk.AccessibleRole.Search;
        WinoA11y.Label(content, Translator.Composer_FindBarLabel);
        _root.SetChild(content);
        _root.SetTransitionType(Gtk.RevealerTransitionType.SlideDown);
        _root.SetRevealChild(false);
        _root.SetVisible(false);

        _subscriptions.Add(GtkSignals.Connect<Gtk.SearchEntry>(h => _query.OnSearchChanged += h, h => _query.OnSearchChanged -= h, (_, _) =>
        {
            if (!_applying) QueryChanged?.Invoke();
        }));
        _subscriptions.Add(GtkSignals.Connect<Gtk.SearchEntry>(h => _query.OnActivate += h, h => _query.OnActivate -= h, (_, _) => FindRequested?.Invoke(EditorFindDirection.Next)));
        _subscriptions.Add(GtkSignals.Connect<Gtk.SearchEntry>(h => _query.OnNextMatch += h, h => _query.OnNextMatch -= h, (_, _) => FindRequested?.Invoke(EditorFindDirection.Next)));
        _subscriptions.Add(GtkSignals.Connect<Gtk.SearchEntry>(h => _query.OnPreviousMatch += h, h => _query.OnPreviousMatch -= h, (_, _) => FindRequested?.Invoke(EditorFindDirection.Previous)));
        _subscriptions.Add(GtkSignals.Connect<Gtk.SearchEntry>(h => _query.OnStopSearch += h, h => _query.OnStopSearch -= h, (_, _) => Closed?.Invoke()));
        _subscriptions.Add(GtkSignals.OnClicked(_previous, () => FindRequested?.Invoke(EditorFindDirection.Previous)));
        _subscriptions.Add(GtkSignals.OnClicked(_next, () => FindRequested?.Invoke(EditorFindDirection.Next)));
        _subscriptions.Add(GtkSignals.OnClicked(_options, ShowOptions));
        _subscriptions.Add(GtkSignals.OnClicked(_done, () => Closed?.Invoke()));
        _subscriptions.Add(GtkSignals.OnClicked(_replace, () => ReplaceRequested?.Invoke()));
        _subscriptions.Add(GtkSignals.OnClicked(_replaceAll, () => ReplaceAllRequested?.Invoke()));
        _subscriptions.Add(GtkSignals.Connect<Gtk.Entry>(h => _replacement.OnActivate += h, h => _replacement.OnActivate -= h, (_, _) => ReplaceRequested?.Invoke()));
    }

    public Gtk.Widget Root => _root;
    public bool IsOpen => _root.GetRevealChild();
    public bool ShowsReplace => _replaceRow.GetRevealChild();
    public string Query => _query.GetText();
    public string Replacement => _replacement.GetText();
    public bool IgnoreCase { get; private set; } = true;
    public bool WholeWords { get; private set; }
    public string CounterText => _counter.GetText();
    public Gtk.Widget QueryEntry => _query;

    public event Action? QueryChanged;
    public event Action<EditorFindDirection>? FindRequested;
    public event Action? ReplaceRequested;
    public event Action? ReplaceAllRequested;
    public event Action? Closed;

    public void Open(bool replace, string? seed)
    {
        if (seed is not null)
        {
            _applying = true;
            try { _query.SetText(seed); }
            finally { _applying = false; }
        }
        _replaceRow.SetRevealChild(replace || (IsOpen && ShowsReplace));
        _root.SetVisible(true);
        _root.SetRevealChild(true);
        _query.GrabFocus();
    }

    public void Close()
    {
        _root.SetRevealChild(false);
        _root.SetVisible(false);
        _replaceRow.SetRevealChild(false);
    }

    public void SetResult(EditorFindResult result)
    {
        var none = result.Count == 0 && Query.Length > 0;
        _counter.SetText(Query.Length == 0 ? string.Empty : none ? Translator.Composer_FindNoMatches : string.Format(Translator.Composer_FindMatchCount, result.Index, result.Count));
        if (none) _query.AddCssClass("error");
        else _query.RemoveCssClass("error");
        _previous.SetSensitive(result.Count > 0);
        _next.SetSensitive(result.Count > 0);
        _replace.SetSensitive(result.Count > 0);
        _replaceAll.SetSensitive(result.Count > 0);
    }

    public void SetOptions(bool ignoreCase, bool wholeWords)
    {
        IgnoreCase = ignoreCase;
        WholeWords = wholeWords;
        QueryChanged?.Invoke();
    }

    public void SetReplacement(string text) => _replacement.SetText(text);

    private void ShowOptions()
    {
        var menu = WinoMenus.Create()
            .Check(Translator.Composer_FindIgnoreCase, IgnoreCase, value => SetOptions(value, WholeWords))
            .Check(Translator.Composer_FindWholeWords, WholeWords, value => SetOptions(IgnoreCase, value))
            .Separator()
            .Item(Translator.Composer_FindShowReplace, () => Open(true, null), icon: WinoIconGlyph.ArrowSwap)
            .Build();
        ContextMenu.Show(_options, menu);
    }

    public void Dispose()
    {
        foreach (var subscription in _subscriptions) subscription.Dispose();
        _subscriptions.Clear();
        QueryChanged = null;
        FindRequested = null;
        ReplaceRequested = null;
        ReplaceAllRequested = null;
        Closed = null;
    }
}
