using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.ToDo;

/// <summary>
/// Title, rename, subtitle and the header buttons of the task surface (Windows ToDoPage header). Renders only; the page
/// binds it and builds the list options menu when <see cref="OptionsRequested"/> fires.
/// </summary>
internal sealed class ToDoListHeader : IDisposable
{
    private readonly Gtk.Box _root = Gtk.Box.New(Gtk.Orientation.Vertical, 2);
    private readonly Gtk.Label _title = Gtk.Label.New(string.Empty);
    private readonly Gtk.Label _subtitle = Gtk.Label.New(string.Empty);
    private readonly Gtk.Button _rename;
    private readonly Gtk.Button _suggestions;
    private readonly Gtk.Button _options;
    private readonly List<IDisposable> _subscriptions = [];

    public ToDoListHeader()
    {
        _root.SetMarginTop(20);
        _root.SetMarginStart(24);
        _root.SetMarginBottom(8);
        _root.SetMarginEnd(20);

        _title.AddCssClass("title-2");
        _title.AddCssClass("accent");
        _title.SetEllipsize(Pango.EllipsizeMode.End);
        _title.SetXalign(0);
        _title.SetSelectable(false);

        _rename = WinoIcons.Button(WinoIconGlyph.Edit, Translator.ToDoPage_RenameList, 12);
        _rename.SetSizeRequest(28, 28);
        _rename.AddCssClass("circular");
        _rename.SetVisible(false);

        _suggestions = WinoIcons.Button(WinoIconGlyph.Lightbulb, Translator.ToDoPage_Suggestions, 15);
        _suggestions.SetVisible(false);

        _options = WinoIcons.Button(WinoIconGlyph.More, Translator.ToDoPage_ListOptions, 15);
        _options.SetVisible(false);

        var spacer = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
        spacer.SetHexpand(true);

        var titleRow = Gtk.Box.New(Gtk.Orientation.Horizontal, 4);
        titleRow.Append(_title);
        titleRow.Append(_rename);
        titleRow.Append(spacer);
        titleRow.Append(_suggestions);
        titleRow.Append(_options);

        _subtitle.AddCssClass("dim-label");
        _subtitle.SetXalign(0);
        _subtitle.SetEllipsize(Pango.EllipsizeMode.End);
        _subtitle.SetVisible(false);

        FilterHost.SetMarginTop(12);
        FilterHost.SetVisible(false);

        _root.Append(titleRow);
        _root.Append(_subtitle);
        _root.Append(FilterHost);

        _subscriptions.Add(GtkSignals.OnClicked(_rename, () => RenameRequested?.Invoke()));
        _subscriptions.Add(GtkSignals.OnClicked(_suggestions, () => SuggestionsRequested?.Invoke()));
        _subscriptions.Add(GtkSignals.OnClicked(_options, () => OptionsRequested?.Invoke(_options)));
    }

    public Gtk.Widget Root => _root;

    /// <summary>The filter row's slot; it shows once it has a child.</summary>
    public Adw.Bin FilterHost { get; } = Adw.Bin.New();

    public Gtk.Widget SuggestionsAnchor => _suggestions;

    public event Action? RenameRequested;

    public event Action? SuggestionsRequested;

    public event Action<Gtk.Widget>? OptionsRequested;

    internal string Title => _title.GetText();

    internal bool IsRenameVisible => _rename.GetVisible();

    internal bool IsOptionsVisible => _options.GetVisible();

    internal bool IsSuggestionsVisible => _suggestions.GetVisible();

    public void SetTitle(string? title) => _title.SetText(title ?? string.Empty);

    public void SetSubtitle(string? subtitle, bool visible)
    {
        _subtitle.SetText(subtitle ?? string.Empty);
        _subtitle.SetVisible(visible && !string.IsNullOrEmpty(subtitle));
    }

    public void SetRenameVisible(bool visible) => _rename.SetVisible(visible);

    public void SetSuggestionsVisible(bool visible) => _suggestions.SetVisible(visible);

    public void SetOptionsVisible(bool visible) => _options.SetVisible(visible);

    public void SetFilter(Gtk.Widget? filter)
    {
        FilterHost.SetChild(filter);
        FilterHost.SetVisible(filter is not null);
    }

    public void Dispose()
    {
        foreach (var subscription in _subscriptions) subscription.Dispose();
        _subscriptions.Clear();
        RenameRequested = null;
        SuggestionsRequested = null;
        OptionsRequested = null;
    }
}
