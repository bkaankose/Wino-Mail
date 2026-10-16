using UIKit;
using Wino.Core.Domain.Enums;
using Wino.Presentation.Controls;
using Wino.Presentation.UIKit;

namespace Wino.Mail.Controls.UIKit.Shell;

/// <summary>
/// The mode switcher card at the foot of the iPad sidebar (Windows AppModeFooterSwitcherControl; UIKit twin of the
/// AppKit <c>WinoModeSwitcher</c>) in the iPad board look (15 Oct 2026): one 44pt tile per mode in the desktop order,
/// the glyph over a small label, on a rounded card. The selected tile shows the Filled glyph and a semibold label in the
/// accent colour on a light accent tint. By default it lists the four app modes, because Settings is never a mode on
/// iOS and iPadOS (it opens from the avatar); pass <c>includeSettings: true</c> for the five desktop segments.
/// </summary>
public sealed class WinoModeSwitcher : WinoSurfaceView
{
    private const double TileHeight = 44;
    private const double TileRadius = 8;
    private const double GlyphSize = 20;
    private const double LabelSize = 10;
    private const double SelectedTint = 0.12;

    private readonly IReadOnlyList<WinoModeSegment> _segments;
    private readonly UIButton[] _tiles;
    private WinoApplicationMode _selected = WinoApplicationMode.Mail;

    /// <summary>Raised when the user picks a tile. The host decides whether it becomes <see cref="Selected"/>.</summary>
    public event EventHandler<WinoApplicationMode>? ModeSelected;

    public WinoModeSwitcher(bool includeSettings = false)
    {
        _segments = includeSettings ? WinoModeSegments.All : WinoModeSegments.AppModes;
        CornerRadius = WinoStyle.GroupRadius + 2;
        Fill = WinoStyle.Dynamic(WinoStyle.Hex(0xFFFFFF, 0.55), WinoStyle.Hex(0xFFFFFF, 0.06));
        Stroke = WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.06), WinoStyle.Hex(0xFFFFFF, 0.07));
        _tiles = _segments.Select(CreateTile).ToArray();
        var row = WinoLayout.HStack(4, _tiles);
        row.Distribution = UIStackViewDistribution.FillEqually;
        row.Alignment = UIStackViewAlignment.Fill;
        WinoLayout.Fill(row, this, 6, 6, 6, 6);
        WinoAccessibility.Label(this, "Mode");
        Refresh();
        WinoStyle.AccentChanged += StyleChanged;
        WinoIcons.StyleChanged += StyleChanged;
    }

    /// <summary>The modes in tile order.</summary>
    public IReadOnlyList<WinoModeSegment> Segments => _segments;

    /// <summary>The mode whose tile is drawn selected.</summary>
    public WinoApplicationMode Selected
    {
        get => _selected;
        set { if (_selected == value) return; _selected = value; Refresh(); }
    }

    private UIButton CreateTile(WinoModeSegment segment)
    {
        var configuration = UIButtonConfiguration.PlainButtonConfiguration;
        configuration.ImagePlacement = NSDirectionalRectEdge.Top;
        configuration.ImagePadding = 2;
        configuration.ContentInsets = new NSDirectionalEdgeInsets(4, 2, 4, 2);
        configuration.TitleLineBreakMode = UILineBreakMode.TailTruncation;
        configuration.Background.CornerRadius = (nfloat)TileRadius;
        var tile = new UIButton { Configuration = configuration, TranslatesAutoresizingMaskIntoConstraints = false };
        tile.HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)TileHeight).Active = true;
        tile.PrimaryActionTriggered += (_, _) => ModeSelected?.Invoke(this, segment.Mode);
        tile.AccessibilityLabel = segment.Title;
        return tile;
    }

    private void StyleChanged(object? sender, EventArgs args) => Refresh();

    private void Refresh()
    {
        for (int index = 0; index < _segments.Count; index++)
        {
            var segment = _segments[index];
            bool selected = segment.Mode == _selected;
            var tile = _tiles[index];
            if (tile.Configuration is not { } configuration) continue;
            var color = selected ? WinoStyle.Accent : WinoStyle.SecondaryText;
            // The glyph is drawn in its colour: a template tint is lost in the colorful icon style.
            configuration.Image = WinoIcons.Image(selected ? segment.Filled : segment.Regular, GlyphSize, selected ? WinoStyle.Accent : null, segment.Title);
            configuration.AttributedTitle = new Foundation.NSAttributedString(segment.Title, new UIStringAttributes
            {
                Font = WinoStyle.Fixed(LabelSize, selected ? UIFontWeight.Semibold : UIFontWeight.Regular),
                ForegroundColor = color
            });
            configuration.BaseForegroundColor = color;
            configuration.Background.BackgroundColor = selected ? WinoStyle.Accent.ColorWithAlpha((nfloat)SelectedTint) : UIColor.Clear;
            tile.Configuration = configuration;
            tile.AccessibilityTraits = selected ? UIAccessibilityTrait.Button | UIAccessibilityTrait.Selected : UIAccessibilityTrait.Button;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            WinoStyle.AccentChanged -= StyleChanged;
            WinoIcons.StyleChanged -= StyleChanged;
            ModeSelected = null;
        }
        base.Dispose(disposing);
    }
}
