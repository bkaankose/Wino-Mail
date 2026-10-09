using AppKit;
using CoreGraphics;
using Foundation;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Shell;

/// <summary>
/// One row of a <see cref="ShellSearchSuggestionList"/>: an optional leading view, image or colour
/// dot, a title, an optional subtitle, and the caller's tag (returned by <see cref="ShellSearchSuggestionList.Chosen"/>).
/// A leading view is owned by the list once shown; create a new one for every <see cref="ShellSearchSuggestionList.Show"/>.
/// </summary>
internal sealed record ShellSearchSuggestion(string Title, string? Subtitle, object? Tag, NSView? Leading = null, NSImage? Image = null, NSColor? Dot = null);

/// <summary>
/// A suggestion list under a text field (the Windows AutoSuggestBox popup): a borderless,
/// non-activating child panel that never becomes key, so the anchor field keeps focus while the
/// user types. The owner forwards ↑/↓ (<see cref="Move"/>), Return (<see cref="TryChoose"/>) and
/// Esc (<see cref="Close"/>) from the field; a click on a row chooses it. The list closes itself
/// when the parent window resigns key, moves or resizes. Main thread only.
/// Used by the toolbar search (calendar, To-Do) and the event composer's attendee field.
/// </summary>
internal sealed class ShellSearchSuggestionList : IDisposable
{
    private const double RowHeight = 40;
    private const double CompactRowHeight = 28;
    private const double MinimumWidth = 320;

    private readonly List<RowView> _rows = [];
    private readonly List<NSObject> _observers = [];
    private SuggestionPanel? _panel;
    private NSStackView? _stack;
    private NSWindow? _parent;
    private int _highlighted = -1;
    private bool _disposed;

    /// <summary>Raised with the chosen row (click, or Return through <see cref="TryChoose"/>). The list is closed first.</summary>
    public event EventHandler<ShellSearchSuggestion>? Chosen;

    /// <summary>Raised when the list closes for any reason.</summary>
    public event EventHandler? Closed;

    /// <summary>Minimum width; the list is at least as wide as its anchor.</summary>
    public double Width { get; set; } = MinimumWidth;

    public bool IsVisible => _panel is not null;

    /// <summary>The highlighted row, or null.</summary>
    public ShellSearchSuggestion? Highlighted => _highlighted >= 0 && _highlighted < _rows.Count ? _rows[_highlighted].Suggestion : null;

    /// <summary>Shows <paramref name="suggestions"/> under <paramref name="anchor"/>; an empty list closes it.</summary>
    public void Show(NSView anchor, IReadOnlyList<ShellSearchSuggestion> suggestions)
    {
        if (_disposed) return;
        if (suggestions.Count == 0 || anchor.Window is not { } window) { Close(); return; }

        if (_panel is null || !ReferenceEquals(_parent, window)) Open(window);
        Fill(suggestions);
        Position(anchor);
    }

    /// <summary>Moves the highlight by <paramref name="delta"/> rows (wrapping). False when the list is closed.</summary>
    public bool Move(int delta)
    {
        if (_panel is null || _rows.Count == 0) return false;
        int next = _highlighted < 0
            ? (delta > 0 ? 0 : _rows.Count - 1)
            : ((_highlighted + delta) % _rows.Count + _rows.Count) % _rows.Count;
        SetHighlight(next, announce: true);
        return true;
    }

    /// <summary>Chooses the highlighted row. False when the list is closed or nothing is highlighted.</summary>
    public bool TryChoose()
    {
        if (Highlighted is not { } suggestion) return false;
        Choose(suggestion);
        return true;
    }

    public void Close()
    {
        if (_panel is null) return;
        var panel = _panel;
        _panel = null;
        foreach (var observer in _observers) NSNotificationCenter.DefaultCenter.RemoveObserver(observer);
        _observers.Clear();
        _parent?.RemoveChildWindow(panel);
        _parent = null;
        panel.OrderOut(null);
        ClearRows();
        _stack = null;
        panel.Close();
        Closed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed) return;
        Close();
        _disposed = true;
        Chosen = null;
        Closed = null;
    }

    private void Open(NSWindow window)
    {
        Close();
        _parent = window;
        var panel = new SuggestionPanel(new CGRect(0, 0, Width, RowHeight),
            NSWindowStyle.Borderless | NSWindowStyle.NonactivatingPanel, NSBackingStore.Buffered, false)
        {
            HasShadow = true,
            BackgroundColor = NSColor.Clear,
            IsOpaque = false,
            HidesOnDeactivate = true,
            Level = window.Level,
            AnimationBehavior = NSWindowAnimationBehavior.UtilityWindow
        };
        panel.ReleaseWhenClosed(false);

        var effect = new NSVisualEffectView
        {
            Material = NSVisualEffectMaterial.Menu,
            BlendingMode = NSVisualEffectBlendingMode.BehindWindow,
            State = NSVisualEffectState.Active,
            WantsLayer = true
        };
        effect.Layer!.CornerRadius = 8;
        effect.Layer.MasksToBounds = true;
        _stack = WinoLayout.VStack(0);
        _stack.EdgeInsets = new NSEdgeInsets(4, 4, 4, 4);
        _stack.AccessibilityElement = true;
        _stack.AccessibilityRole = NSAccessibilityRoles.ListRole;
        WinoLayout.Fill(_stack, effect);
        panel.ContentView = effect;
        _panel = panel;

        window.AddChildWindow(panel, NSWindowOrderingMode.Above);
        var center = NSNotificationCenter.DefaultCenter;
        foreach (var name in new[] { NSWindow.DidResignKeyNotification, NSWindow.DidMoveNotification, NSWindow.DidResizeNotification, NSWindow.WillCloseNotification })
            _observers.Add(center.AddObserver(name, _ => Close(), window));
    }

    private void Fill(IReadOnlyList<ShellSearchSuggestion> suggestions)
    {
        ClearRows();
        if (_stack is null) return;
        foreach (var suggestion in suggestions)
        {
            var row = new RowView(suggestion, string.IsNullOrEmpty(suggestion.Subtitle) ? CompactRowHeight : RowHeight);
            row.Hovered += (_, _) => SetHighlight(_rows.IndexOf(row), announce: false);
            // A click arrives inside the row's own MouseUp; close the panel after that event finishes.
            row.Clicked += (_, _) => NSApplication.SharedApplication.BeginInvokeOnMainThread(() => { if (!_disposed && _panel is not null) Choose(row.Suggestion); });
            _stack.AddArrangedSubview(row);
            row.WidthAnchor.ConstraintEqualTo(_stack.WidthAnchor, 1, -8).Active = true;
            _rows.Add(row);
        }
        _highlighted = -1;
    }

    private void ClearRows()
    {
        foreach (var row in _rows)
        {
            _stack?.RemoveArrangedSubview(row);
            row.RemoveFromSuperview();
        }
        _rows.Clear();
        _highlighted = -1;
    }

    private void Position(NSView anchor)
    {
        if (_panel is null || anchor.Window is not { } window) return;
        var inWindow = anchor.ConvertRectToView(anchor.Bounds, null);
        var onScreen = window.ConvertRectToScreen(inWindow);
        double height = 8 + _rows.Sum(row => row.RowHeight);
        double width = Math.Max(Width, onScreen.Width);
        var frame = new CGRect(onScreen.X, onScreen.Y - 4 - height, width, height);
        if (window.Screen?.VisibleFrame is { } visible && frame.GetMaxX() > visible.GetMaxX())
            frame.X = (nfloat)Math.Max(visible.X, visible.GetMaxX() - width);
        _panel.SetFrame(frame, true);
        _panel.OrderFront(null);
    }

    private void SetHighlight(int index, bool announce)
    {
        if (index < 0 || index >= _rows.Count) return;
        if (_highlighted >= 0 && _highlighted < _rows.Count) _rows[_highlighted].IsHighlighted = false;
        _highlighted = index;
        var row = _rows[index];
        row.IsHighlighted = true;
        if (announce)
            NSAccessibility.PostNotification(row, new NSString("AXAnnouncementRequested"),
                NSDictionary.FromObjectAndKey(new NSString(row.AccessibilityLabel ?? row.Suggestion.Title), NSAccessibilityNotificationUserInfoKeys.AnnouncementKey));
    }

    private void Choose(ShellSearchSuggestion suggestion)
    {
        Close();
        Chosen?.Invoke(this, suggestion);
    }

    /// <summary>A panel that never takes key or main status, so the anchor field keeps focus.</summary>
    private sealed class SuggestionPanel(CGRect rect, NSWindowStyle style, NSBackingStore backing, bool defer) : NSPanel(rect, style, backing, defer)
    {
        public override bool CanBecomeKeyWindow => false;
        public override bool CanBecomeMainWindow => false;
    }

    private sealed class RowView : NSView
    {
        private readonly NSTextField _title;
        private readonly NSTextField? _subtitle;
        private bool _highlighted;

        public RowView(ShellSearchSuggestion suggestion, double height)
        {
            Suggestion = suggestion;
            RowHeight = height;
            TranslatesAutoresizingMaskIntoConstraints = false;
            WantsLayer = true;
            Layer!.CornerRadius = 5;

            var children = new List<NSView>();
            if (suggestion.Leading is { } leading) children.Add(leading);
            else if (suggestion.Image is { } image)
            {
                var imageView = new NSImageView { Image = image, TranslatesAutoresizingMaskIntoConstraints = false };
                WinoLayout.Size(imageView, 16, 16);
                children.Add(imageView);
            }
            else if (suggestion.Dot is { } dot)
            {
                var swatch = new WinoSurfaceView { Fill = dot, CornerRadius = 4 };
                WinoLayout.Size(swatch, 8, 8);
                children.Add(swatch);
            }

            _title = WinoStyle.Label(suggestion.Title, WinoStyle.BodyStrong, WinoStyle.PrimaryText);
            _title.LineBreakMode = NSLineBreakMode.TruncatingTail;
            _title.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
            NSView text = _title;
            if (!string.IsNullOrEmpty(suggestion.Subtitle))
            {
                _subtitle = WinoStyle.Label(suggestion.Subtitle, WinoStyle.Caption, WinoStyle.SecondaryText);
                _subtitle.LineBreakMode = NSLineBreakMode.TruncatingTail;
                _subtitle.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
                text = WinoLayout.VStack(1, _title, _subtitle);
                ((NSStackView)text).Alignment = NSLayoutAttribute.Leading;
            }
            children.Add(text);
            children.Add(WinoLayout.Spacer());
            var stack = WinoLayout.HStack(8, children.ToArray());
            stack.EdgeInsets = new NSEdgeInsets(0, 8, 0, 8);
            WinoLayout.Fill(stack, this);
            HeightAnchor.ConstraintEqualTo((nfloat)height).Active = true;

            AccessibilityElement = true;
            AccessibilityRole = NSAccessibilityRoles.ButtonRole;
            AccessibilityLabel = string.IsNullOrEmpty(suggestion.Subtitle) ? suggestion.Title : $"{suggestion.Title}, {suggestion.Subtitle}";
        }

        public ShellSearchSuggestion Suggestion { get; }
        public double RowHeight { get; }
        public event EventHandler? Hovered;
        public event EventHandler? Clicked;

        public bool IsHighlighted
        {
            get => _highlighted;
            set
            {
                _highlighted = value;
                AccessibilitySelected = value;
                NeedsDisplay = true;
                // The selected-content colour follows the accent and stays legible in light and dark mode.
                var text = value ? NSColor.SelectedMenuItemText : WinoStyle.PrimaryText;
                _title.TextColor = text;
                if (_subtitle is not null) _subtitle.TextColor = value ? NSColor.SelectedMenuItemText.ColorWithAlphaComponent((nfloat)0.85) : WinoStyle.SecondaryText;
            }
        }

        public override bool WantsUpdateLayer => true;

        public override void UpdateLayer()
        {
            if (Layer is null) return;
            CGColor? fill = null;
            EffectiveAppearance.PerformAsCurrentDrawingAppearance(() => fill = _highlighted ? WinoStyle.Accent.CGColor : null);
            Layer.BackgroundColor = fill;
        }

        public override bool AcceptsFirstMouse(NSEvent? theEvent) => true;

        public override void UpdateTrackingAreas()
        {
            base.UpdateTrackingAreas();
            foreach (var area in TrackingAreas()) RemoveTrackingArea(area);
            AddTrackingArea(new NSTrackingArea(Bounds, NSTrackingAreaOptions.MouseEnteredAndExited | NSTrackingAreaOptions.ActiveAlways | NSTrackingAreaOptions.InVisibleRect, this, null));
        }

        public override void MouseEntered(NSEvent theEvent) => Hovered?.Invoke(this, EventArgs.Empty);
        public override void MouseDown(NSEvent theEvent) { }

        public override void MouseUp(NSEvent theEvent)
        {
            if (Bounds.Contains(ConvertPointFromView(theEvent.LocationInWindow, null))) Clicked?.Invoke(this, EventArgs.Empty);
        }

        public override bool AccessibilityPerformPress()
        {
            Clicked?.Invoke(this, EventArgs.Empty);
            return true;
        }
    }
}
