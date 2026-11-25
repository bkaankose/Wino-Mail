using System.Text;
using Wino.Mail.Controls.Core.MailRow;
using Wino.Mail.Controls.Linux.Common;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Style;

namespace Wino.Mail.Controls.Linux.MailList;

/// <summary>
/// The tile line of a <see cref="WinoMailRow"/>: wrapping chips under the preview in Medium and Spacious, 8 px category
/// dots and 18 px intelligence badges on line 1 in Compact (Windows <c>CompactTileTemplate</c>, macOS <c>_compactTiles</c>).
/// Widgets are pooled per row; an unchanged tile list returns at once.
/// </summary>
public sealed class WinoTileLine
{
    private readonly Gtk.Box _detailedSlot;
    private readonly Gtk.Box _compactSlot;
    private readonly List<WinoChip> _chips = [];
    private readonly List<CompactTile> _compact = [];
    private Adw.WrapBox? _wrap;
    private WinoMailRowTile[] _shown = [];
    private bool _isCompact;
    private bool _first = true;
    private WinoMailRowDensity _density;

    public WinoTileLine(Gtk.Box detailedSlot, Gtk.Box compactSlot)
    {
        _detailedSlot = detailedSlot;
        _compactSlot = compactSlot;
    }

    public int ShownCount => _shown.Length;

    public bool IsCompact => _isCompact;

    public void Apply(WinoMailRowTile[] tiles, WinoMailRowDensity density)
    {
        var count = Math.Min(tiles.Length, WinoMailRowMetrics.MaxTiles);
        var compact = density == WinoMailRowDensity.Compact;
        if (!_first && compact == _isCompact && density == _density && Same(tiles, count)) return;
        _first = false;
        _isCompact = compact;
        _density = density;
        _shown = tiles.Length == count ? tiles : tiles[..count];

        if (compact) ApplyCompact();
        else ApplyDetailed(density);

        _detailedSlot.SetVisible(!compact && count > 0);
        _compactSlot.SetVisible(compact && count > 0);
    }

    /// <summary><c>compact|count|text,text,…</c></summary>
    public string Describe()
    {
        var text = new StringBuilder();
        text.Append(_isCompact).Append('|').Append(_shown.Length).Append('|');
        for (var index = 0; index < _shown.Length; index++)
        {
            if (index > 0) text.Append(',');
            text.Append(_shown[index].Text);
        }
        return text.ToString();
    }

    private bool Same(WinoMailRowTile[] tiles, int count)
    {
        if (count != _shown.Length) return false;
        for (var index = 0; index < count; index++)
        {
            if (!Equals(tiles[index], _shown[index])) return false;
        }
        return true;
    }

    private void ApplyDetailed(WinoMailRowDensity density)
    {
        if (_wrap is null)
        {
            _wrap = Adw.WrapBox.New();
            _wrap.SetChildSpacing((int)WinoMailRowMetrics.TileSpacing);
            _wrap.SetLineSpacing((int)WinoMailRowMetrics.TileLineSpacing);
            _detailedSlot.Append(_wrap);
        }
        _detailedSlot.SetMarginTop((int)WinoMailRowMetrics.TileLineTopMargin(density));

        while (_chips.Count < _shown.Length)
        {
            var chip = new WinoChip((int)WinoMailRowMetrics.TileHeight);
            _chips.Add(chip);
            _wrap.Append(chip.Root);
        }
        for (var index = 0; index < _chips.Count; index++)
        {
            var chip = _chips[index];
            if (index >= _shown.Length)
            {
                chip.Root.SetVisible(false);
                continue;
            }
            var tile = _shown[index];
            chip.Text = tile.Text;
            chip.Tooltip = tile.Tooltip;
            if (tile.IsIntelligence)
            {
                chip.SetColors(null, null);
                chip.Glyph = tile.Glyph;
                chip.ShowsDot = string.IsNullOrEmpty(tile.Glyph);
                chip.IsWarning = tile.IsWarning;
            }
            else
            {
                chip.SetColors(tile.BackgroundHex, tile.ForegroundHex, accent: true);
                chip.Glyph = null;
                chip.ShowsDot = false;
                chip.IsWarning = false;
            }
            chip.Root.SetVisible(true);
        }
    }

    private void ApplyCompact()
    {
        while (_compact.Count < _shown.Length)
        {
            var tile = new CompactTile();
            _compact.Add(tile);
            _compactSlot.Append(tile.Root);
        }
        for (var index = 0; index < _compact.Count; index++)
        {
            if (index >= _shown.Length) _compact[index].Root.SetVisible(false);
            else _compact[index].Apply(_shown[index]);
        }
    }

    // One widget per slot that switches between an 8 px category dot and an 18 px intelligence badge, so the slot order
    // never has to be rebuilt when the kind sequence changes.
    private sealed class CompactTile
    {
        private readonly Gtk.Box _root;
        private readonly Gtk.Label _glyph;
        private readonly Gtk.Box _dot;
        private string? _fillClass;
        private string _name = string.Empty;
        private string? _glyphValue;
        private bool? _isIntelligence;
        private bool _warning;
        private bool _accent;

        public CompactTile()
        {
            _root = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
            _root.SetValign(Gtk.Align.Center);
            _root.SetHalign(Gtk.Align.Center);
            // The centred children expand inside the badge; an explicit false keeps that from widening line 1.
            _root.SetHexpand(false);
            _glyph = WinoIcons.Label(string.Empty, 10);
            _glyph.SetHexpand(true);
            _glyph.SetHalign(Gtk.Align.Center);
            _glyph.SetValign(Gtk.Align.Center);
            _glyph.SetVisible(false);
            _dot = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
            _dot.AddCssClass(WinoCssClass.ChipDot);
            _dot.SetHexpand(true);
            _dot.SetHalign(Gtk.Align.Center);
            _dot.SetValign(Gtk.Align.Center);
            _dot.SetVisible(false);
            _root.Append(_glyph);
            _root.Append(_dot);
        }

        public Gtk.Box Root => _root;

        public void Apply(WinoMailRowTile tile)
        {
            if (_isIntelligence != tile.IsIntelligence)
            {
                _isIntelligence = tile.IsIntelligence;
                Toggle(WinoCssClass.TileBadge, tile.IsIntelligence);
                Toggle(WinoCssClass.TileDot, !tile.IsIntelligence);
                if (tile.IsIntelligence) _root.SetSizeRequest(18, 18);
                else _root.SetSizeRequest(8, 8);
            }

            var fill = tile.IsIntelligence ? null : WinoTint.FillClassFor(tile.BackgroundHex, null);
            if (!string.Equals(fill, _fillClass, StringComparison.Ordinal))
            {
                if (_fillClass is not null) _root.RemoveCssClass(_fillClass);
                if (fill is not null) _root.AddCssClass(fill);
                _fillClass = fill;
            }
            var accent = !tile.IsIntelligence && fill is null;
            if (accent != _accent)
            {
                _accent = accent;
                Toggle(WinoCssClass.ChipAccent, accent);
            }

            var glyph = tile.IsIntelligence && !string.IsNullOrEmpty(tile.Glyph) ? tile.Glyph : null;
            if (!string.Equals(glyph, _glyphValue, StringComparison.Ordinal))
            {
                _glyphValue = glyph;
                if (glyph is not null) WinoIcons.SetGlyph(_glyph, glyph);
            }
            _glyph.SetVisible(glyph is not null);
            _dot.SetVisible(tile.IsIntelligence && glyph is null);

            var warning = tile.IsIntelligence && tile.IsWarning;
            if (warning != _warning)
            {
                _warning = warning;
                Toggle(WinoCssClass.ChipWarning, warning);
            }

            var name = tile.Tooltip ?? tile.Text;
            if (!string.Equals(name, _name, StringComparison.Ordinal))
            {
                _name = name;
                _root.SetTooltipText(name.Length == 0 ? null : name);
                WinoA11y.Label(_root, name);
            }
            _root.SetVisible(true);
        }

        private void Toggle(string cssClass, bool on)
        {
            if (on) _root.AddCssClass(cssClass);
            else _root.RemoveCssClass(cssClass);
        }
    }
}
