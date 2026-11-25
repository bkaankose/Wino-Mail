using Wino.Core.Domain.Enums;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Style;

namespace Wino.Mail.Controls.Linux.Common;

/// <summary>
/// A small pill with a text and an optional glyph or dot: category tiles (coloured through <see cref="WinoTint.FillClassFor"/>)
/// and intelligence tiles (neutral fill, glyph). The text is plain, never markup.
/// </summary>
public sealed class WinoChip
{
    private readonly Gtk.Box _root;
    private readonly Gtk.Box _dot;
    private readonly Gtk.Label _glyph;
    private readonly Gtk.Label _text;
    private string _textValue = string.Empty;
    private string? _glyphValue;
    private string? _tooltip;
    private string _accessibleName = string.Empty;
    private string? _fillClass;
    private bool _accent;
    private bool _showsDot;
    private bool _isWarning;
    private int _maxWidthChars = 24;

    public WinoChip(int height = 18)
    {
        _root = Gtk.Box.New(Gtk.Orientation.Horizontal, 4);
        _root.AddCssClass(WinoCssClass.Chip);
        _root.AddCssClass(WinoCssClass.ChipTile);
        _root.SetSizeRequest(-1, height);
        _root.SetValign(Gtk.Align.Center);
        _root.SetHalign(Gtk.Align.Start);

        _dot = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
        _dot.AddCssClass(WinoCssClass.ChipDot);
        _dot.SetValign(Gtk.Align.Center);
        _dot.SetVisible(false);

        _glyph = WinoIcons.Label(string.Empty, 11);
        _glyph.AddCssClass(WinoCssClass.ChipGlyph);
        _glyph.SetValign(Gtk.Align.Center);
        _glyph.SetVisible(false);

        _text = Gtk.Label.New(string.Empty);
        _text.SetEllipsize(Pango.EllipsizeMode.End);
        _text.SetSingleLineMode(true);
        _text.SetMaxWidthChars(_maxWidthChars);
        _text.SetValign(Gtk.Align.Center);

        _root.Append(_dot);
        _root.Append(_glyph);
        _root.Append(_text);
    }

    public Gtk.Widget Root => _root;

    public string Text
    {
        get => _textValue;
        set
        {
            if (string.Equals(_textValue, value, StringComparison.Ordinal)) return;
            _textValue = value;
            _text.SetText(value);
            UpdateName();
        }
    }

    /// <summary>A WinoIcons glyph string drawn at 11 px; null or empty hides it.</summary>
    public string? Glyph
    {
        get => _glyphValue;
        set
        {
            if (string.IsNullOrEmpty(value)) value = null;
            if (string.Equals(_glyphValue, value, StringComparison.Ordinal)) return;
            _glyphValue = value;
            if (value is not null) WinoIcons.SetGlyph(_glyph, value);
            _glyph.SetVisible(value is not null);
        }
    }

    public void SetGlyph(WinoIconGlyph icon) => Glyph = WinoIcons.Glyph(icon);

    /// <summary>A 6 px dot before the text, for an intelligence tile without a glyph.</summary>
    public bool ShowsDot
    {
        get => _showsDot;
        set
        {
            if (_showsDot == value) return;
            _showsDot = value;
            _dot.SetVisible(value);
        }
    }

    public bool IsWarning
    {
        get => _isWarning;
        set
        {
            if (_isWarning == value) return;
            _isWarning = value;
            if (value) _root.AddCssClass(WinoCssClass.ChipWarning);
            else _root.RemoveCssClass(WinoCssClass.ChipWarning);
        }
    }

    public int MaxWidthChars
    {
        get => _maxWidthChars;
        set
        {
            if (_maxWidthChars == value) return;
            _maxWidthChars = value;
            _text.SetMaxWidthChars(value);
        }
    }

    /// <summary>Tooltip and accessible name; null uses <see cref="Text"/>.</summary>
    public string? Tooltip
    {
        get => _tooltip;
        set
        {
            if (string.Equals(_tooltip, value, StringComparison.Ordinal)) return;
            _tooltip = value;
            UpdateName();
        }
    }

    /// <summary>A null background gives the neutral fill; <paramref name="accent"/> gives the accent tint instead.</summary>
    public void SetColors(string? backgroundHex, string? foregroundHex, bool accent = false)
    {
        var next = WinoTint.FillClassFor(backgroundHex, foregroundHex);
        if (!string.Equals(next, _fillClass, StringComparison.Ordinal))
        {
            if (_fillClass is not null) _root.RemoveCssClass(_fillClass);
            if (next is not null) _root.AddCssClass(next);
            _fillClass = next;
            // The neutral .wino-chip fill and a fill class have the same specificity in different providers, so only one is set.
            if (next is null) _root.AddCssClass(WinoCssClass.Chip);
            else _root.RemoveCssClass(WinoCssClass.Chip);
        }
        var useAccent = accent && next is null;
        if (useAccent == _accent) return;
        _accent = useAccent;
        if (useAccent) _root.AddCssClass(WinoCssClass.ChipAccent);
        else _root.RemoveCssClass(WinoCssClass.ChipAccent);
    }

    private void UpdateName()
    {
        var name = _tooltip ?? _textValue;
        if (string.Equals(name, _accessibleName, StringComparison.Ordinal)) return;
        _accessibleName = name;
        _root.SetTooltipText(name.Length == 0 ? null : name);
        WinoA11y.Label(_root, name);
    }
}
