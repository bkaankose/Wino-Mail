using AppKit;
using CoreAnimation;
using CoreFoundation;
using CoreGraphics;
using Foundation;
using Wino.Core.Domain.Enums;
using Wino.Presentation.AppKit;
using Wino.Presentation.Controls;

namespace Wino.Mail.Controls.AppKit.Shell;

/// <summary>
/// The Windows WinoModeSwitcher at the bottom of the shell pane: a floating card with one glyph button per
/// visible mode. An accent thumb springs between the buttons and the new glyph pops; the app modes share
/// the width and Settings keeps a fixed slot behind a divider. Modes that appear or leave fold their width
/// away while the others slide into place. Settings opens its own window on the Mac, so the thumb never
/// rests on it: <see cref="Selected"/> ignores it and the gear only raises <see cref="ModeSelected"/>.
/// </summary>
public sealed class WinoModeSwitcher : NSView
{
    private const double CardTop = 6;
    private const double CardSide = 8;
    private const double CardBottom = 10;
    private const double CardPadding = 5;
    private const double CardStroke = 1;
    private const double CardRadius = 9;
    private const double ItemHeight = 32;
    private const double ItemSpacing = 2;
    private const double PinnedWidth = 40;
    private const double DividerInset = 4;
    private const double SlideDuration = 0.3;
    private const double ThumbResizeDuration = 0.26;
    private const double ThumbFadeDuration = 0.16;

    private readonly CardView _card = new();
    private readonly ThumbView _thumb = new();
    private readonly WinoSurfaceView _divider = new() { Fill = WinoStyle.Separator, TranslatesAutoresizingMaskIntoConstraints = true };
    private readonly List<ModeItemView> _items;
    private WinoApplicationMode _selected = WinoApplicationMode.Mail;
    private double _arrangedWidth = -1;
    private bool _thumbPlaced;
    private int _foldGeneration;

    /// <summary>Raised when the user picks a mode. The host decides whether it becomes <see cref="Selected"/>.</summary>
    public event EventHandler<WinoApplicationMode>? ModeSelected;

    public WinoModeSwitcher()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        WantsLayer = true;
        FocusRingType = NSFocusRingType.Exterior;
        AccessibilityRole = NSAccessibilityRoles.RadioGroupRole;
        WinoAccessibility.Label(this, "Mode");
        _items = WinoModeSegments.All.Select(segment => new ModeItemView(segment)).ToList();
        _card.AddSubview(_thumb);
        _card.AddSubview(_divider);
        foreach (var item in _items) _card.AddSubview(item);
        AddSubview(_card);
        ApplySelection(animated: false);
        WinoStyle.AccentWatchers.Add(this, static switcher => switcher.AccentChanged());
    }

    private static double CardHeight => CardPadding * 2 + CardStroke * 2 + ItemHeight;

    private static bool CanAnimate => !NSWorkspace.SharedWorkspace.AccessibilityDisplayShouldReduceMotion;

    public override bool IsFlipped => true;

    public override bool AcceptsFirstResponder() => NSApplication.SharedApplication.FullKeyboardAccessEnabled;

    public override CGSize IntrinsicContentSize => new(NoIntrinsicMetric, CardTop + CardHeight + CardBottom);

    /// <summary>The app mode the thumb rests on. Settings is never selected here; the gear is a plain button.</summary>
    public WinoApplicationMode Selected
    {
        get => _selected;
        set
        {
            if (value == WinoApplicationMode.Settings || _selected == value) return;
            _selected = value;
            ApplySelection(animated: Window is not null);
        }
    }

    /// <summary>
    /// Shows only <paramref name="visible"/> (the shell's <c>AppModeVisibility.VisibleModes</c>) plus Settings, which is
    /// always reachable (Windows AppModeFooterSwitcherControl); null shows every segment.
    /// </summary>
    public void SetVisibleModes(IReadOnlyCollection<WinoApplicationMode>? visible)
    {
        bool changed = false;
        foreach (var item in _items)
        {
            bool shown = visible is null || item.Segment.Mode == WinoApplicationMode.Settings || visible.Contains(item.Segment.Mode);
            if (item.IsShown == shown) continue;
            item.IsShown = shown;
            changed = true;
        }
        if (!changed) return;
        bool animated = Window is not null && Bounds.Width > 0 && CanAnimate;
        ArrangeItems(animated);
        PlaceThumb(animated);
    }

    public override void Layout()
    {
        base.Layout();
        var width = Bounds.Width;
        _card.Frame = new CGRect(CardSide, CardTop, Math.Max(0, width - CardSide * 2), CardHeight);
        if (Math.Abs(width - _arrangedWidth) < 0.5) return;
        _arrangedWidth = width;
        ArrangeItems(animated: false);
        PlaceThumb(animated: false);
    }

    public override void ViewDidMoveToWindow()
    {
        base.ViewDidMoveToWindow();
        // Whatever the layout looks like when the control comes back, the thumb lands on it directly.
        if (Window is null) _thumbPlaced = false;
    }

    public override void DrawFocusRingMask() => NSBezierPath.FromRoundedRect(_card.Frame, (nfloat)CardRadius, (nfloat)CardRadius).Fill();

    public override CGRect FocusRingMaskBounds => _card.Frame;

    public override void KeyDown(NSEvent theEvent)
    {
        var modes = _items.Where(item => item.IsShown && item.Segment.Mode != WinoApplicationMode.Settings).Select(item => item.Segment.Mode).ToList();
        int index = modes.IndexOf(_selected);
        if (modes.Count == 0 || index < 0) { base.KeyDown(theEvent); return; }
        int next = theEvent.KeyCode switch
        {
            123 or 126 => (index - 1 + modes.Count) % modes.Count,
            124 or 125 => (index + 1) % modes.Count,
            _ => -1
        };
        if (next < 0) { base.KeyDown(theEvent); return; }
        if (modes[next] != _selected) ModeSelected?.Invoke(this, modes[next]);
    }

    private void ItemClicked(ModeItemView item)
    {
        if (!item.IsShown) return;
        ModeSelected?.Invoke(this, item.Segment.Mode);
    }

    private void AccentChanged()
    {
        _thumb.NeedsDisplay = true;
        foreach (var item in _items) item.Refresh();
    }

    private void ApplySelection(bool animated)
    {
        bool motion = animated && CanAnimate;
        foreach (var item in _items) item.SetSelected(item.Segment.Mode == _selected, motion);
        PlaceThumb(motion);
    }

    #region Layout

    /// <summary>
    /// Lays the visible items out left to right: the app modes share the width, Settings keeps its slot at the
    /// trailing end behind the divider. A hidden item collapses to zero width at the slot it would occupy, so
    /// it folds in or out of place instead of appearing from nowhere.
    /// </summary>
    private void ArrangeItems(bool animated)
    {
        var inner = _card.Bounds.Width - (CardPadding + CardStroke) * 2;
        if (inner <= 0) return;
        var y = CardPadding + CardStroke;
        var shared = _items.Where(item => item.Segment.Mode != WinoApplicationMode.Settings).ToList();
        var pinned = _items.First(item => item.Segment.Mode == WinoApplicationMode.Settings);
        int shownCount = shared.Count(item => item.IsShown);
        double pinnedSpan = pinned.IsShown ? PinnedWidth + ItemSpacing * 2 + 1 : 0;
        double each = shownCount == 0 ? 0 : Math.Max(0, (inner - pinnedSpan - ItemSpacing * (shownCount - 1)) / shownCount);

        foreach (var item in _items)
            if (item.IsShown) item.Hidden = false;

        int generation = ++_foldGeneration;
        if (animated)
        {
            NSAnimationContext.BeginGrouping();
            NSAnimationContext.CurrentContext.Duration = SlideDuration;
            NSAnimationContext.CurrentContext.TimingFunction = StandardEasing();
        }

        double x = CardPadding + CardStroke;
        bool first = true;
        foreach (var item in shared)
        {
            if (item.IsShown)
            {
                if (!first) x += ItemSpacing;
                first = false;
                SetFrame(item, new CGRect(x, y, each, ItemHeight), 1, animated);
                x += each;
            }
            else
            {
                SetFrame(item, new CGRect(x, y, 0, ItemHeight), 0, animated);
            }
        }

        _divider.Hidden = !pinned.IsShown || shownCount == 0;
        var dividerX = x + ItemSpacing;
        SetFrame(_divider, new CGRect(dividerX, y + DividerInset, 1, ItemHeight - DividerInset * 2), 1, animated);
        SetFrame(pinned, new CGRect(CardPadding + CardStroke + inner - PinnedWidth, y, pinned.IsShown ? PinnedWidth : 0, ItemHeight), pinned.IsShown ? 1 : 0, animated);

        if (animated)
        {
            NSAnimationContext.EndGrouping();
            // Folded items stay in the tree until the slide ends, then leave the hit-test path.
            DispatchQueue.MainQueue.DispatchAfter(new DispatchTime(DispatchTime.Now, TimeSpan.FromSeconds(SlideDuration)), () =>
            {
                if (generation != _foldGeneration || Handle == IntPtr.Zero) return;
                foreach (var item in _items) item.Hidden = !item.IsShown;
            });
        }
        else
        {
            foreach (var item in _items) item.Hidden = !item.IsShown;
        }
    }

    private static void SetFrame(NSView view, CGRect frame, double alpha, bool animated)
    {
        if (!animated)
        {
            view.Frame = frame;
            view.AlphaValue = (nfloat)alpha;
            return;
        }
        var animator = (NSView)view.Animator;
        animator.Frame = frame;
        animator.AlphaValue = (nfloat)alpha;
    }

    private static CAMediaTimingFunction StandardEasing() => CAMediaTimingFunction.FromControlPoints(0.1f, 0.9f, 0.2f, 1f);

    #endregion

    #region Thumb

    private void PlaceThumb(bool animated)
    {
        var item = _items.FirstOrDefault(entry => entry.Segment.Mode == _selected && entry.IsShown);
        if (item is null)
        {
            HideThumb(animated);
            return;
        }

        bool motion = animated && _thumbPlaced && !_thumb.Hidden;
        MoveThumb(item.Frame, motion);
        _thumbPlaced = true;
        ShowThumb(animated);
    }

    /// <summary>
    /// The model frame jumps to the target; the explicit animations carry the presentation there. Position
    /// takes the Windows spring (damping ratio 0.72) and the size an ease-out, as on Windows.
    /// </summary>
    private void MoveThumb(CGRect target, bool animated)
    {
        var layer = _thumb.Layer;
        if (layer is null)
        {
            _thumb.Frame = target;
            return;
        }

        var presentation = layer.PresentationLayer ?? layer;
        var fromPosition = presentation.Position;
        var fromSize = presentation.Bounds.Size;
        layer.RemoveAnimation("move");
        layer.RemoveAnimation("resize");
        _thumb.Frame = target;
        if (!animated) return;

        var spring = new CASpringAnimation { KeyPath = "position" };
        spring.Mass = 1;
        spring.Stiffness = 620;
        spring.Damping = 36;
        spring.From = NSValue.FromCGPoint(fromPosition);
        spring.To = NSValue.FromCGPoint(layer.Position);
        spring.Duration = spring.SettlingDuration;
        layer.AddAnimation(spring, "move");

        if (Math.Abs(fromSize.Width - target.Width) < 0.5 && Math.Abs(fromSize.Height - target.Height) < 0.5) return;
        var resize = CABasicAnimation.FromKeyPath("bounds.size");
        resize.From = NSValue.FromCGSize(fromSize);
        resize.To = NSValue.FromCGSize(layer.Bounds.Size);
        resize.Duration = ThumbResizeDuration;
        resize.TimingFunction = CAMediaTimingFunction.FromName(CAMediaTimingFunction.EaseOut);
        layer.AddAnimation(resize, "resize");
    }

    private void ShowThumb(bool animated)
    {
        if (!_thumb.Hidden && _thumb.AlphaValue >= 1) return;
        _thumb.Hidden = false;
        if (!animated) { _thumb.AlphaValue = 1; return; }
        NSAnimationContext.BeginGrouping();
        NSAnimationContext.CurrentContext.Duration = ThumbFadeDuration;
        ((NSView)_thumb.Animator).AlphaValue = 1;
        NSAnimationContext.EndGrouping();
    }

    private void HideThumb(bool animated)
    {
        _thumbPlaced = false;
        if (_thumb.Hidden) return;
        if (!animated) { _thumb.AlphaValue = 0; _thumb.Hidden = true; return; }
        NSAnimationContext.BeginGrouping();
        NSAnimationContext.CurrentContext.Duration = ThumbFadeDuration;
        ((NSView)_thumb.Animator).AlphaValue = 0;
        NSAnimationContext.EndGrouping();
    }

    #endregion

    public override void ViewDidChangeEffectiveAppearance()
    {
        base.ViewDidChangeEffectiveAppearance();
        foreach (var item in _items) item.Refresh();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) WinoStyle.AccentWatchers.Remove(this);
        base.Dispose(disposing);
    }

    private static bool IsDark(NSAppearance appearance)
        => appearance.FindBestMatch([NSAppearance.NameAqua, NSAppearance.NameDarkAqua]) == NSAppearance.NameDarkAqua;

    /// <summary>The floating card: a translucent surface over the pane vibrancy with a soft drop shadow.</summary>
    private sealed class CardView : WinoSurfaceView
    {
        private static readonly NSColor CardFill = WinoStyle.Dynamic(WinoStyle.Hex(0xFFFFFF, 0.78), WinoStyle.Hex(0xFFFFFF, 0.075));

        public CardView()
        {
            TranslatesAutoresizingMaskIntoConstraints = true;
            Fill = CardFill;
            Stroke = WinoStyle.ZoneStroke;
            CornerRadius = CardRadius;
        }

        public override void UpdateLayer()
        {
            base.UpdateLayer();
            if (Layer is not { } layer) return;
            layer.MasksToBounds = false;
            layer.ShadowColor = NSColor.Black.CGColor;
            layer.ShadowOpacity = IsDark(EffectiveAppearance) ? 0.35f : 0.12f;
            layer.ShadowRadius = 8;
            layer.ShadowOffset = new CGSize(0, -2);
        }
    }

    /// <summary>The accent thumb under the selected glyph; its tint follows the theme accent and the appearance.</summary>
    private sealed class ThumbView : WinoSurfaceView
    {
        public ThumbView()
        {
            TranslatesAutoresizingMaskIntoConstraints = true;
            CornerRadius = ItemRadius;
            Hidden = true;
            AlphaValue = 0;
        }

        public override void UpdateLayer()
        {
            base.UpdateLayer();
            if (Layer is not { } layer) return;
            bool dark = IsDark(EffectiveAppearance);
            var accent = WinoStyle.Accent;
            layer.BackgroundColor = accent.ColorWithAlphaComponent((nfloat)(dark ? 0.22 : 0.16)).CGColor;
            layer.BorderColor = accent.ColorWithAlphaComponent((nfloat)(dark ? 0.42 : 0.34)).CGColor;
            layer.BorderWidth = 1;
            layer.MasksToBounds = false;
            layer.ShadowColor = NSColor.Black.CGColor;
            layer.ShadowOpacity = dark ? 0.25f : 0.08f;
            layer.ShadowRadius = 3;
            layer.ShadowOffset = new CGSize(0, -1);
        }
    }

    private const double ItemRadius = 6;
    private const double GlyphSize = 18;
    private const double GlyphPopDuration = 0.38;

    /// <summary>
    /// One glyph button. Hover and press keep their own subtle fill; the selected look lives on the thumb beneath,
    /// so selection only recolours the glyph and swaps it for the Filled twin.
    /// </summary>
    private sealed class ModeItemView : WinoSurfaceView
    {
        private static readonly NSColor PressFill = WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.06), WinoStyle.Hex(0xFFFFFF, 0.1));

        private readonly WinoIconView _icon;
        private bool _hovered;
        private bool _pressed;
        private bool _selected;

        public ModeItemView(WinoModeSegment segment)
        {
            Segment = segment;
            TranslatesAutoresizingMaskIntoConstraints = true;
            CornerRadius = ItemRadius;
            ToolTip = $"{segment.Title} ({segment.Shortcut})";
            AccessibilityRole = segment.Mode == WinoApplicationMode.Settings ? NSAccessibilityRoles.ButtonRole : NSAccessibilityRoles.RadioButtonRole;
            WinoAccessibility.Label(this, segment.Title);
            _icon = new WinoIconView(segment.Regular, GlyphSize)
            {
                TranslatesAutoresizingMaskIntoConstraints = true,
                AutoresizingMask = NSViewResizingMask.WidthSizable | NSViewResizingMask.HeightSizable,
                WantsLayer = true
            };
            AddSubview(_icon);
            AddTrackingArea(new NSTrackingArea(CGRect.Empty, NSTrackingAreaOptions.MouseEnteredAndExited | NSTrackingAreaOptions.ActiveInKeyWindow | NSTrackingAreaOptions.InVisibleRect, this, null));
            Refresh();
        }

        public WinoModeSegment Segment { get; }

        public bool IsShown { get; set; } = true;

        private WinoModeSwitcher? Owner => Superview?.Superview as WinoModeSwitcher;

        public override void Layout()
        {
            base.Layout();
            _icon.Frame = Bounds;
        }

        public void SetSelected(bool selected, bool animated)
        {
            bool changed = _selected != selected;
            _selected = selected;
            _icon.Icon = selected ? Segment.Filled : Segment.Regular;
            Refresh();
            if (selected && changed && animated) PlayGlyphPop();
        }

        public void Refresh()
        {
            Fill = _pressed ? PressFill : _hovered ? WinoStyle.RowHoverFill : null;
            _icon.Tint = _selected ? WinoStyle.Accent : _hovered ? WinoStyle.PrimaryText : WinoStyle.SecondaryText;
            _icon.AlphaValue = _selected && _pressed ? 0.7f : 1f;
            AccessibilityValue = _selected ? NSNumber.FromInt32(1) : NSNumber.FromInt32(0);
        }

        /// <summary>The Windows glyph pop: shrink to 0.8, overshoot to 1.12, settle, around the glyph centre.</summary>
        private void PlayGlyphPop()
        {
            if (_icon.Layer is not { } layer || Bounds.Width <= 0 || Bounds.Height <= 0) return;
            var cx = Bounds.Width / 2;
            var cy = Bounds.Height / 2;
            CATransform3D Scale(double factor) => CATransform3D.MakeTranslation(cx, cy, 0).Scale((nfloat)factor, (nfloat)factor, 1).Translate(-cx, -cy, 0);

            var pop = CAKeyFrameAnimation.FromKeyPath("transform");
            pop.Values = [NSValue.FromCATransform3D(CATransform3D.Identity), NSValue.FromCATransform3D(Scale(0.8)), NSValue.FromCATransform3D(Scale(1.12)), NSValue.FromCATransform3D(CATransform3D.Identity)];
            pop.KeyTimes = [NSNumber.FromDouble(0), NSNumber.FromDouble(0.3), NSNumber.FromDouble(0.65), NSNumber.FromDouble(1)];
            pop.TimingFunctions = [CAMediaTimingFunction.FromControlPoints(0.4f, 0f, 0.8f, 0.6f), StandardEasing(), StandardEasing()];
            pop.Duration = GlyphPopDuration;
            layer.RemoveAnimation("pop");
            layer.AddAnimation(pop, "pop");
        }

        public override void MouseEntered(NSEvent theEvent) { _hovered = true; Refresh(); }

        public override void MouseExited(NSEvent theEvent) { _hovered = false; _pressed = false; Refresh(); }

        public override void MouseDown(NSEvent theEvent) { _pressed = true; Refresh(); }

        public override void MouseUp(NSEvent theEvent)
        {
            bool inside = _pressed && Bounds.Contains(ConvertPointFromView(theEvent.LocationInWindow, null));
            _pressed = false;
            Refresh();
            if (inside) Owner?.ItemClicked(this);
        }

        public override bool AccessibilityPerformPress()
        {
            Owner?.ItemClicked(this);
            return true;
        }
    }
}

/// <summary>
/// The pane surface behind the shell menu. With a Wino theme the wallpaper shows through,
/// blurred within the window over <c>ThemeBackdropView</c> and softened by a light or dark veil;
/// with the Default theme it is the native behind-window sidebar material.
/// </summary>
public sealed class WinoPaneBackdropView : NSView
{
    private readonly NSVisualEffectView _effect = new()
    {
        State = NSVisualEffectState.FollowsWindowActiveState,
        TranslatesAutoresizingMaskIntoConstraints = false
    };

    private readonly WinoSurfaceView _veil = new()
    {
        Fill = WinoStyle.Dynamic(WinoStyle.Hex(0xFFFFFF, 0.22), WinoStyle.Hex(0x14141A, 0.28))
    };

    public WinoPaneBackdropView()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        WinoLayout.Fill(_effect, this);
        WinoLayout.Fill(_veil, this);
        Apply();
        WinoStyle.BackdropWatchers.Add(this, static view => view.BackdropChanged(null, EventArgs.Empty));
    }

    private void BackdropChanged(object? sender, EventArgs args) => Apply();

    private void Apply()
    {
        // The translucent window material alone keeps the native sidebar material; a theme wallpaper blurs within the window.
        bool themed = WinoStyle.HasBackdrop && !WinoStyle.IsWindowMaterialOnly;
        _effect.BlendingMode = themed ? NSVisualEffectBlendingMode.WithinWindow : NSVisualEffectBlendingMode.BehindWindow;
        _effect.Material = themed ? NSVisualEffectMaterial.HudWindow : NSVisualEffectMaterial.Sidebar;
        _effect.Hidden = false;
        _veil.Hidden = !themed;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) WinoStyle.BackdropWatchers.Remove(this);
        base.Dispose(disposing);
    }
}
