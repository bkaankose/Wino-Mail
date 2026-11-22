using Wino.Core.Domain.Enums;
using Wino.Presentation.Linux;

namespace Wino.Mail.Controls.Linux.Settings;

public sealed record WinoThemeTileModel(Guid Id, string Name, string? CompatibilityText, string? ImagePath,
    uint? GradientStart, uint? GradientEnd, bool IsCustom);

/// <summary>
/// One theme of the gallery: a toggle button with the preview (wallpaper, gradient or the plain window background under
/// a window miniature), the name and the compatibility. A custom theme also gets an Edit button, a sibling of the toggle
/// (a button inside a button gets no clicks). The page groups the toggles and pushes gradients and textures.
/// </summary>
public sealed class WinoThemeTile
{
    public const int PreviewWidth = 150;
    public const int PreviewHeight = 92;

    private readonly Gtk.Box _preview;
    private readonly Gtk.Picture _picture;
    private string? _gradientClass;

    public WinoThemeTile(WinoThemeTileModel model, string editLabel)
    {
        Model = model;

        _picture = Gtk.Picture.New();
        _picture.SetContentFit(Gtk.ContentFit.Cover);
        _picture.SetCanShrink(true);
        _picture.SetVisible(false);
        _picture.AccessibleRole = Gtk.AccessibleRole.Presentation;

        _preview = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
        _preview.AddCssClass("wino-theme-preview");
        _preview.SetSizeRequest(PreviewWidth, PreviewHeight);
        _preview.SetOverflow(Gtk.Overflow.Hidden);
        _preview.Append(Clamped(_picture, PreviewWidth));

        var miniature = Miniature();
        var check = new WinoIcon(WinoIconGlyph.Checkmark, 12).Root;
        var badge = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
        badge.AddCssClass("wino-theme-check");
        badge.SetHalign(Gtk.Align.End);
        badge.SetValign(Gtk.Align.Start);
        badge.SetMarginTop(6);
        badge.SetMarginEnd(6);
        check.SetHexpand(true);
        check.SetHalign(Gtk.Align.Center);
        badge.Append(check);

        var overlay = Gtk.Overlay.New();
        overlay.SetChild(_preview);
        overlay.AddOverlay(miniature);
        overlay.AddOverlay(badge);
        overlay.SetOverflow(Gtk.Overflow.Hidden);
        overlay.SetHalign(Gtk.Align.Center);

        var name = Gtk.Label.New(model.Name);
        name.SetEllipsize(Pango.EllipsizeMode.End);
        name.SetMaxWidthChars(18);
        var content = Gtk.Box.New(Gtk.Orientation.Vertical, 4);
        content.Append(overlay);
        content.Append(name);
        if (!string.IsNullOrEmpty(model.CompatibilityText))
        {
            var compatibility = Gtk.Label.New(model.CompatibilityText);
            compatibility.AddCssClass("caption");
            compatibility.AddCssClass("dim-label");
            content.Append(compatibility);
        }

        Root = Gtk.ToggleButton.New();
        Root.AddCssClass("flat");
        Root.AddCssClass("wino-theme-tile");
        Root.SetChild(content);
        WinoA11y.Label(Root, model.Name);

        Container = Gtk.Box.New(Gtk.Orientation.Vertical, 2);
        Container.SetValign(Gtk.Align.Start);
        Container.Append(Root);
        if (model.IsCustom)
        {
            EditButton = Gtk.Button.NewWithLabel(editLabel);
            EditButton.AddCssClass("flat");
            EditButton.AddCssClass("caption");
            EditButton.SetHalign(Gtk.Align.Center);
            WinoA11y.Label(EditButton, $"{editLabel} {model.Name}");
            Container.Append(EditButton);
        }

        if (model.GradientStart is { } start && model.GradientEnd is { } end) SetGradient(start, end);
    }

    public Gtk.ToggleButton Root { get; }

    /// <summary>The widget the gallery lays out: the toggle and, for a custom theme, its Edit button.</summary>
    public Gtk.Box Container { get; }

    public Gtk.Button? EditButton { get; }

    public WinoThemeTileModel Model { get; }

    public void SetGradient(uint start, uint end)
    {
        if (_gradientClass is not null) _preview.RemoveCssClass(_gradientClass);
        _gradientClass = WinoSwatchCss.Gradient(start, end);
        _preview.AddCssClass(_gradientClass);
    }

    public void SetTexture(Gdk.Texture? texture)
    {
        _picture.SetPaintable(texture);
        _picture.SetVisible(texture is not null);
    }

    /// <summary>
    /// A picture's natural width is its texture's, twice the preview for sharpness; the clamp caps it so the wrap box
    /// lays tiles out at preview size.
    /// </summary>
    public static Adw.Clamp Clamped(Gtk.Picture picture, int width)
    {
        picture.SetHexpand(true);
        var clamp = Adw.Clamp.New();
        clamp.SetMaximumSize(width);
        clamp.SetTighteningThreshold(width);
        clamp.SetHexpand(true);
        clamp.SetChild(picture);
        return clamp;
    }

    /// <summary>A sidebar strip and a content card over the preview, so every theme reads as a window.</summary>
    private static Gtk.Box Miniature()
    {
        WinoSwatchCss.EnsureInstalled();
        var sidebar = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        sidebar.AddCssClass("wino-theme-mini-sidebar");
        sidebar.SetSizeRequest(34, -1);
        var card = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        card.AddCssClass("wino-theme-mini-content");
        card.SetHexpand(true);
        card.SetMarginTop(8);
        card.SetMarginBottom(8);
        card.SetMarginStart(8);
        card.SetMarginEnd(8);
        var window = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
        window.SetMarginTop(10);
        window.SetMarginBottom(10);
        window.SetMarginStart(10);
        window.SetMarginEnd(10);
        window.Append(sidebar);
        window.Append(card);
        window.SetCanTarget(false);
        return window;
    }
}
