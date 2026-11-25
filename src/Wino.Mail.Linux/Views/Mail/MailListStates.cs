using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Mail;

internal enum MailListEmptyState { FolderEmpty, NoSearchResult, NoOnlineSearchResult }

/// <summary>The empty list: a glyph from the icon font and one line of text (Adw.StatusPage takes icon names only).</summary>
internal sealed class MailListStates
{
    private readonly Gtk.Box _root;
    private readonly WinoIcon _icon;
    private readonly Gtk.Label _text;
    private MailListEmptyState? _shown;

    public MailListStates()
    {
        _root = Gtk.Box.New(Gtk.Orientation.Vertical, 10);
        _root.SetHalign(Gtk.Align.Center);
        _root.SetValign(Gtk.Align.Center);
        _root.SetMarginTop(20);
        _root.SetMarginBottom(20);
        _root.SetMarginStart(20);
        _root.SetMarginEnd(20);
        _icon = new WinoIcon(WinoIconGlyph.Inbox, 48, WinoIconMode.Mono);
        _icon.Root.AddCssClass("dim-label");
        _icon.Root.SetHalign(Gtk.Align.Center);
        _text = Gtk.Label.New(string.Empty);
        _text.AddCssClass("title-4");
        _text.SetWrap(true);
        _text.SetJustify(Gtk.Justification.Center);
        _text.SetMaxWidthChars(28);
        _root.Append(_icon.Root);
        _root.Append(_text);
    }

    public Gtk.Widget Root => _root;

    public void Show(MailListEmptyState state, bool force = false)
    {
        if (_shown == state && !force) return;
        _shown = state;
        var (glyph, text) = state switch
        {
            MailListEmptyState.NoSearchResult => (WinoIconGlyph.Find, Translator.NoMessageCrieteria),
            MailListEmptyState.NoOnlineSearchResult => (WinoIconGlyph.GlobeSearch, Translator.NoMessageCrieteria),
            _ => (WinoIconGlyph.Inbox, Translator.NoMessageEmptyFolder),
        };
        _icon.SetIcon(glyph);
        _text.SetText(text);
        WinoA11y.Label(_root, text);
    }

    /// <summary>Shows the current state again with the texts of the current language.</summary>
    public void Relocalize()
    {
        if (_shown is { } state) Show(state, force: true);
    }
}
