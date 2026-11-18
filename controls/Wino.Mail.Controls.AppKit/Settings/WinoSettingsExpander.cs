using AppKit;
using CoreAnimation;
using Wino.Core.Domain.Enums;
using Wino.Presentation.AppKit;

namespace Wino.Mail.Controls.AppKit.Settings;

/// <summary>
/// macOS counterpart of the Windows SettingsExpander: a header card with a chevron, and nested
/// rows on a recessed fill under it, indented to the header text column and separated by hairlines.
/// Header and rows cast one shadow together.
/// Expansion animates the rows open and closed; nested rows keep their bindings.
/// </summary>
public sealed class WinoSettingsExpander : NSView
{
    private readonly WinoSettingsCard _header;
    private const double AnimationDuration = 0.22;

    private readonly WinoSurfaceView _itemsSurface;
    private readonly NSView _itemsClip;
    private readonly NSLayoutConstraint _clipHeight;
    private readonly NSLayoutConstraint _clipFollowsItems;
    private readonly NSStackView _items;
    private int _animationGeneration;
    private bool _isExpanded;
    private bool _isEnabled = true;

    public WinoSettingsExpander(string header, string? description = null, WinoIconGlyph icon = WinoIconGlyph.None, NSView? content = null, bool isExpanded = false)
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        WantsLayer = true;
        _header = new WinoSettingsCard(header, description, icon, content) { IsClickable = true, IsExpanderHeader = true, OwnsShadow = false };
        _header.Activated += (_, _) => IsExpanded = !IsExpanded;

        _itemsSurface = new WinoSurfaceView
        {
            Fill = WinoSettingsStyle.ExpanderItemsFill,
            Stroke = WinoSettingsStyle.CardStroke,
            CornerRadius = WinoSettingsStyle.CardRadius,
            Corners = CACornerMask.MinXMaxYCorner | CACornerMask.MaxXMaxYCorner
        };
        _items = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Vertical,
            Alignment = NSLayoutAttribute.Leading,
            Spacing = 0,
            Distribution = NSStackViewDistribution.Fill,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        WinoLayout.Fill(_items, _itemsSurface);

        // The clip animates its height between 0 and the rows' height; at rest when expanded it follows the rows,
        // so rows that change size later still resize the expander.
        _itemsClip = new NSView { TranslatesAutoresizingMaskIntoConstraints = false, WantsLayer = true };
        _itemsClip.Layer!.MasksToBounds = true;
        _itemsClip.AddSubview(_itemsSurface);
        _itemsSurface.TopAnchor.ConstraintEqualTo(_itemsClip.TopAnchor).Active = true;
        _itemsSurface.LeadingAnchor.ConstraintEqualTo(_itemsClip.LeadingAnchor).Active = true;
        _itemsSurface.TrailingAnchor.ConstraintEqualTo(_itemsClip.TrailingAnchor).Active = true;
        _clipHeight = _itemsClip.HeightAnchor.ConstraintEqualTo(0);
        _clipFollowsItems = _itemsClip.BottomAnchor.ConstraintEqualTo(_itemsSurface.BottomAnchor);

        var stack = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Vertical,
            Alignment = NSLayoutAttribute.Leading,
            Spacing = 0,
            Distribution = NSStackViewDistribution.Fill,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        stack.AddArrangedSubview(_header);
        stack.AddArrangedSubview(_itemsClip);
        _header.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor).Active = true;
        _itemsClip.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor).Active = true;
        WinoLayout.Fill(stack, this);

        SetExpanded(isExpanded, animated: false);
        AccessibilityElement = false;
    }

    public WinoSettingsCard HeaderCard => _header;

    public override void Layout()
    {
        base.Layout();
        UpdateShadow();
    }

    public override void ViewDidChangeEffectiveAppearance()
    {
        base.ViewDidChangeEffectiveAppearance();
        UpdateShadow();
    }

    private void UpdateShadow()
    {
        if (Layer is { } layer && !Bounds.IsEmpty)
            WinoSettingsStyle.ApplyCardShadow(layer, Bounds, WinoIcons.IsDark(EffectiveAppearance), hovered: false);
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetExpanded(value, animated: Window is not null);
    }

    private static bool CanAnimate => !NSWorkspace.SharedWorkspace.AccessibilityDisplayShouldReduceMotion;

    private void SetExpanded(bool expanded, bool animated)
    {
        _isExpanded = expanded;
        _header.IsExpanded = expanded;
        int generation = ++_animationGeneration;

        if (!animated || !CanAnimate)
        {
            ApplyRestingState(expanded);
            IsExpandedChanged?.Invoke(this, expanded);
            return;
        }

        var root = Window?.ContentView ?? this;
        root.LayoutSubtreeIfNeeded();
        double from = _itemsClip.Hidden ? 0 : _itemsClip.Frame.Height;

        _itemsClip.Hidden = false;
        _clipFollowsItems.Active = false;
        _clipHeight.Constant = (nfloat)from;
        _clipHeight.Active = true;
        root.LayoutSubtreeIfNeeded();
        double to = expanded ? _itemsSurface.Frame.Height : 0;

        _itemsSurface.AlphaValue = expanded ? 0 : 1;
        NSAnimationContext.RunAnimation(context =>
        {
            context.Duration = AnimationDuration;
            context.TimingFunction = CAMediaTimingFunction.FromName(CAMediaTimingFunction.EaseInEaseOut);
            context.AllowsImplicitAnimation = true;
            _clipHeight.Constant = (nfloat)to;
            _itemsSurface.AlphaValue = expanded ? 1 : 0;
            root.LayoutSubtreeIfNeeded();
        }, () =>
        {
            if (generation != _animationGeneration) return;
            ApplyRestingState(expanded);
        });
        IsExpandedChanged?.Invoke(this, expanded);
    }

    private void ApplyRestingState(bool expanded)
    {
        _itemsSurface.AlphaValue = 1;
        _itemsClip.Hidden = !expanded;
        _clipHeight.Active = !expanded;
        _clipHeight.Constant = 0;
        _clipFollowsItems.Active = expanded;
    }

    public event EventHandler<bool>? IsExpandedChanged;

    /// <summary>Disables the header content and every nested row; the header stays clickable so rows remain readable.</summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            _isEnabled = value;
            _header.SetContentEnabled(value);
            foreach (var view in _items.ArrangedSubviews) SetRowsEnabled(view, value);
        }
    }

    private static void SetRowsEnabled(NSView view, bool enabled)
    {
        switch (view)
        {
            case WinoSettingsCard card: card.IsEnabled = enabled; return;
            case WinoLabeledSwitch toggle: toggle.IsEnabled = enabled; return;
            case NSControl control: control.Enabled = enabled; return;
        }
        foreach (var child in view.Subviews) SetRowsEnabled(child, enabled);
    }

    /// <summary>Adds a nested row. Cards become flat rows indented to the header text; other views get the same insets.</summary>
    public void Add(NSView row)
    {
        if (_items.ArrangedSubviews.Length > 0)
        {
            var separator = new WinoSeparator { Fill = WinoSettingsStyle.CardStroke };
            _items.AddArrangedSubview(separator);
            separator.WidthAnchor.ConstraintEqualTo(_items.WidthAnchor).Active = true;
        }

        row.TranslatesAutoresizingMaskIntoConstraints = false;
        if (row is WinoSettingsCard card)
        {
            card.IsNested = true;
            _items.AddArrangedSubview(card);
            card.WidthAnchor.ConstraintEqualTo(_items.WidthAnchor).Active = true;
            return;
        }

        AddHosted(row, 10, WinoSettingsStyle.NestedIndent, 10, WinoSettingsStyle.CardPadding);
    }

    /// <summary>Adds a nested row with custom insets (rows that carry their own leading picture, item lists).</summary>
    public void Add(NSView row, double top, double leading, double bottom, double trailing)
    {
        if (_items.ArrangedSubviews.Length > 0)
        {
            var separator = new WinoSeparator { Fill = WinoSettingsStyle.CardStroke };
            _items.AddArrangedSubview(separator);
            separator.WidthAnchor.ConstraintEqualTo(_items.WidthAnchor).Active = true;
        }
        row.TranslatesAutoresizingMaskIntoConstraints = false;
        AddHosted(row, top, leading, bottom, trailing);
    }

    private void AddHosted(NSView row, double top, double leading, double bottom, double trailing)
    {
        var host = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(row, host, top, leading, bottom, trailing);
        _items.AddArrangedSubview(host);
        host.WidthAnchor.ConstraintEqualTo(_items.WidthAnchor).Active = true;
    }
}
