using Wino.Core.Domain.Enums;

namespace Wino.Presentation.Linux;

/// <summary>
/// One Wino glyph. In the colour modes the monochrome glyph is drawn over the colour glyph: the GPU renderers draw the
/// colour font's foreground layer white, so the outline must come from the monochrome font. The mode is fixed at
/// construction; style changes are a stylesheet reload in <see cref="WinoIcons"/>.
/// </summary>
public sealed class WinoIcon
{
    private readonly Gtk.Label _mono;
    private readonly Gtk.Label? _color;
    private string _glyph;
    private string? _tintHex;
    private int _size;

    public WinoIcon(WinoIconGlyph icon = WinoIconGlyph.None, int size = 16, WinoIconMode mode = WinoIconMode.FollowStyle)
    {
        Mode = mode;
        _size = size;
        _glyph = WinoIcons.Glyph(icon);
        _mono = WinoIcons.Label(_glyph, size);

        if (mode == WinoIconMode.Mono)
        {
            Root = _mono;
        }
        else
        {
            _color = WinoIcons.GlyphLabel(_glyph, size, mode == WinoIconMode.Colorful ? WinoIcons.ColorClass : WinoIcons.FollowClass);
            var overlay = Gtk.Overlay.New();
            overlay.SetChild(_color);
            overlay.AddOverlay(_mono);
            overlay.SetHalign(Gtk.Align.Center);
            overlay.SetValign(Gtk.Align.Center);
            Root = overlay;
        }

        Root.SetCanTarget(false);
        Root.SetFocusable(false);
    }

    public Gtk.Widget Root { get; }

    public WinoIconMode Mode { get; }

    public void SetIcon(WinoIconGlyph icon) => Glyph = WinoIcons.Glyph(icon);

    /// <summary>An empty glyph keeps the widget's size, so rows do not jump when a glyph appears.</summary>
    public string Glyph
    {
        get => _glyph;
        set
        {
            if (_glyph == value) return;
            _glyph = value;
            Apply();
        }
    }

    /// <summary>A <c>#RRGGBB</c> colour for the outline; null follows the current foreground colour.</summary>
    public string? TintHex
    {
        get => _tintHex;
        set
        {
            if (_tintHex == value) return;
            _tintHex = value;
            Apply();
        }
    }

    public int Size
    {
        get => _size;
        set
        {
            if (_size == value) return;
            var previous = WinoIcons.SizeClass(_size);
            _size = value;
            var next = WinoIcons.SizeClass(value);
            foreach (var label in (Gtk.Label?[])[_mono, _color])
            {
                if (label is null) continue;
                label.RemoveCssClass(previous);
                label.AddCssClass(next);
            }
        }
    }

    private void Apply()
    {
        WinoIcons.SetGlyph(_mono, _glyph, _tintHex);
        _color?.SetText(_glyph);
    }
}
