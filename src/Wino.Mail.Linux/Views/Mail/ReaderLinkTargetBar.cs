using Wino.Core.Domain;
using Wino.Editor;

namespace Wino.Mail.Linux.Views.Mail;

/// <summary>
/// The address of the hovered or focused link at the bottom start of the body (Windows <c>ShowLinkTarget</c>, macOS
/// <c>ReaderLinkTargetBar</c>); the Safe Links target when the link is wrapped. It never takes the pointer.
/// </summary>
internal sealed class ReaderLinkTargetBar
{
    private readonly Gtk.Box _root = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
    private readonly Gtk.Label _label = Gtk.Label.New(string.Empty);

    public ReaderLinkTargetBar()
    {
        _root.AccessibleRole = Gtk.AccessibleRole.Presentation;
        _label.AccessibleRole = Gtk.AccessibleRole.Presentation;
        _root.AddCssClass("wino-link-target");
        _root.SetHalign(Gtk.Align.Start);
        _root.SetValign(Gtk.Align.End);
        _root.SetHexpand(false);
        _root.SetMarginStart(8);
        _root.SetMarginBottom(8);
        _root.SetMarginEnd(48);
        _root.SetCanTarget(false);
        _root.SetCanFocus(false);
        _root.SetVisible(false);
        _label.AddCssClass("caption");
        _label.SetEllipsize(Pango.EllipsizeMode.Middle);
        _label.SetMaxWidthChars(90);
        _label.SetWidthChars(-1);
        _label.SetSingleLineMode(true);
        _label.SetXalign(0);
        _root.Append(_label);
    }

    public Gtk.Widget Root => _root;

    public string Text => _root.GetVisible() ? _label.GetText() : string.Empty;

    public void Show(RendererLinkHoverEventArgs args)
    {
        if (args.Uri is null)
        {
            Clear();
            return;
        }
        // Replace, not string.Format: a URL may contain braces.
        var text = args.SafeLinkTarget is { } target ? Translator.Reader_ProtectedLinkFormat.Replace("{0}", target, StringComparison.Ordinal) : args.Uri;
        _label.SetText(text);
        _root.SetVisible(true);
    }

    public void Clear()
    {
        _label.SetText(string.Empty);
        _root.SetVisible(false);
    }
}
