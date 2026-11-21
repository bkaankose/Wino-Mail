using System.Globalization;
using Wino.Presentation.Linux.Style;

namespace Wino.Mail.Linux.Views.Mail;

/// <summary>
/// The drag icon of a mail drag (macOS <c>MailDragPayload.CreateImage</c>, Windows <c>DragUIOverride.Caption</c>): a card
/// with a picture of the dragged row, stacked edges for two or more mails, a count badge, and a caption chip while the
/// pointer is over the sidebar. A live widget, so the caption changes without redrawing an image.
/// </summary>
internal sealed class MailDragCard
{
    public const int CardWidth = 250;
    public const int CardHeight = 56;

    private readonly Gtk.Label _caption;

    public MailDragCard(int count, Gdk.Paintable? rowImage, string fallbackTitle)
    {
        var root = Gtk.Box.New(Gtk.Orientation.Vertical, 6);
        root.AddCssClass(WinoCssClass.DragCardRoot);

        var card = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
        card.AddCssClass(WinoCssClass.DragCard);
        if (count == 2) card.AddCssClass(WinoCssClass.DragCard2);
        else if (count >= 3) card.AddCssClass(WinoCssClass.DragCard3);
        card.SetSizeRequest(CardWidth, CardHeight);
        card.SetOverflow(Gtk.Overflow.Hidden);
        card.SetHalign(Gtk.Align.Start);
        if (rowImage is not null)
        {
            var picture = Gtk.Picture.New();
            picture.SetPaintable(rowImage);
            picture.SetContentFit(Gtk.ContentFit.Cover);
            picture.SetHalign(Gtk.Align.Start);
            picture.SetCanShrink(true);
            picture.SetHexpand(true);
            card.Append(picture);
        }
        else
        {
            var title = Gtk.Label.New(fallbackTitle);
            title.SetEllipsize(Pango.EllipsizeMode.End);
            title.SetMaxWidthChars(28);
            title.SetXalign(0);
            title.SetMarginStart(12);
            title.SetMarginEnd(12);
            card.Append(title);
        }

        var overlay = Gtk.Overlay.New();
        overlay.SetChild(card);
        overlay.SetHalign(Gtk.Align.Start);
        var badge = Gtk.Label.New(count.ToString(CultureInfo.CurrentCulture));
        badge.AddCssClass(WinoCssClass.DragBadge);
        badge.SetHalign(Gtk.Align.End);
        badge.SetValign(Gtk.Align.Start);
        badge.SetVisible(count > 1);
        overlay.AddOverlay(badge);

        _caption = Gtk.Label.New(string.Empty);
        _caption.AddCssClass(WinoCssClass.DragCaption);
        _caption.SetHalign(Gtk.Align.Start);
        _caption.SetVisible(false);

        root.Append(overlay);
        root.Append(_caption);
        Root = root;
    }

    public Gtk.Widget Root { get; }

    public void SetCaption(string? caption, bool refused)
    {
        _caption.SetText(caption ?? string.Empty);
        _caption.SetVisible(!string.IsNullOrEmpty(caption));
        if (refused) _caption.AddCssClass(WinoCssClass.DragCaptionRefused);
        else _caption.RemoveCssClass(WinoCssClass.DragCaptionRefused);
    }
}
