using AppKit;
using CoreAnimation;
using CoreGraphics;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.AppKit.Common;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Mail;

/// <summary>Reader state "no message selected": glyph, title and keyboard hints (design board "Reader states").</summary>
internal sealed class MailIdleView : NSView
{
    private readonly NSTextField _title;
    private readonly NSTextField _message;
    private readonly NSButton _action;

    public MailIdleView()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        var glyph = new WinoIconView(WinoIconGlyph.Mail, 56, WinoStyle.TertiaryText);
        _title = WinoStyle.Label(Translator.NoMailSelected, NSFont.SystemFontOfSize(15, NSFontWeight.Semibold), WinoStyle.PrimaryText);
        _title.Alignment = NSTextAlignment.Center;
        // No translation key exists for the Mac keyboard hints yet.
        _message = WinoStyle.Label("Use ↑ ↓ to move, ⌘R to reply, ⌫ to delete.", NSFont.SystemFontOfSize(12), WinoStyle.SecondaryText, 0);
        _message.Alignment = NSTextAlignment.Center;
        _action = new NSButton { BezelStyle = NSBezelStyle.Rounded, Hidden = true, TranslatesAutoresizingMaskIntoConstraints = false };
        _action.Activated += (_, _) => ActionInvoked?.Invoke(this, EventArgs.Empty);
        var stack = WinoLayout.VStack(14, glyph, _title, _message, _action);
        stack.Alignment = NSLayoutAttribute.CenterX;
        stack.TranslatesAutoresizingMaskIntoConstraints = false;
        AddSubview(stack);
        NSLayoutConstraint.ActivateConstraints(
        [
            stack.CenterXAnchor.ConstraintEqualTo(CenterXAnchor),
            stack.CenterYAnchor.ConstraintEqualTo(CenterYAnchor),
            stack.LeadingAnchor.ConstraintGreaterThanOrEqualTo(LeadingAnchor, 24),
            stack.TrailingAnchor.ConstraintLessThanOrEqualTo(TrailingAnchor, -24),
            _message.WidthAnchor.ConstraintLessThanOrEqualTo(320)
        ]);
    }

    public event EventHandler? ActionInvoked;

    /// <summary>Replaces the default copy, used for the account and mail-empty states of the idle page.</summary>
    public void SetContent(string title, string message, string? actionTitle)
    {
        _title.StringValue = title;
        _message.StringValue = message;
        _action.Title = actionTitle ?? string.Empty;
        _action.Hidden = string.IsNullOrEmpty(actionTitle);
    }
}

/// <summary>Reader state "multi-selection": overlapping avatars, count, summary and bulk actions.</summary>
internal sealed class MailMultiSelectionView : NSView
{
    private readonly NSStackView _avatars;
    private readonly NSTextField _title;
    private readonly NSTextField _summary;

    public MailMultiSelectionView()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        WantsLayer = true;
        _avatars = WinoLayout.HStack(-12);
        _title = WinoStyle.Label(string.Empty, NSFont.SystemFontOfSize(17, NSFontWeight.Bold));
        _title.Alignment = NSTextAlignment.Center;
        _summary = WinoStyle.Label(string.Empty, WinoStyle.Body, WinoStyle.SecondaryText, 0);
        _summary.Alignment = NSTextAlignment.Center;

        var archive = Action(Translator.MailOperation_Archive, ShellCommand.Archive);
        var delete = Action(Translator.MailOperation_Delete, ShellCommand.Delete);
        var read = Action(Translator.MailOperation_MarkAsRead, ShellCommand.ToggleRead);
        var move = Action($"{Translator.MailOperation_Move}…", ShellCommand.Move);
        var grid = NSGridView.Create(new NSView[][] { [archive, delete], [read, move] });
        grid.ColumnSpacing = 8;
        grid.RowSpacing = 8;
        grid.X = NSGridCellPlacement.Fill;
        grid.TranslatesAutoresizingMaskIntoConstraints = false;
        WinoLayout.Size(grid, 220, -1);

        var stack = WinoLayout.VStack(16, _avatars, _title, _summary, grid);
        stack.Alignment = NSLayoutAttribute.CenterX;
        stack.TranslatesAutoresizingMaskIntoConstraints = false;
        AddSubview(stack);
        NSLayoutConstraint.ActivateConstraints(
        [
            stack.CenterXAnchor.ConstraintEqualTo(CenterXAnchor),
            stack.CenterYAnchor.ConstraintEqualTo(CenterYAnchor),
            stack.LeadingAnchor.ConstraintGreaterThanOrEqualTo(LeadingAnchor, 24),
            stack.TrailingAnchor.ConstraintLessThanOrEqualTo(TrailingAnchor, -24)
        ]);
    }

    private NSButton Action(string title, ShellCommand command)
    {
        var button = new NSButton { Title = title, BezelStyle = NSBezelStyle.Rounded, TranslatesAutoresizingMaskIntoConstraints = false };
        button.Activated += (_, _) => ActionInvoked?.Invoke(button, command);
        return button;
    }

    /// <summary>Raised with the bulk command; the sender is the button, used as the Move popover anchor.</summary>
    public event EventHandler<ShellCommand>? ActionInvoked;

    public void Update(IReadOnlyList<(string Name, string Address)> senders, int count, int unread, int flagged, int attachments)
    {
        foreach (var view in _avatars.ArrangedSubviews)
        {
            _avatars.RemoveArrangedSubview(view);
            view.RemoveFromSuperview();
            view.Dispose();
        }
        foreach (var (name, address) in senders.Take(3))
        {
            var picture = new WinoContactPicture(44) { Stroke = NSColor.WindowBackground, StrokeWidth = 2 };
            picture.SetIdentity(string.IsNullOrWhiteSpace(name) ? address : name, string.IsNullOrWhiteSpace(address) ? name : address);
            _avatars.AddArrangedSubview(picture);
        }
        _title.StringValue = string.Format(Translator.MailsSelected, count);
        var parts = new List<string>();
        if (unread > 0) parts.Add($"{Translator.FilteringOption_Unread}: {unread}");
        if (flagged > 0) parts.Add($"{Translator.FilteringOption_Flagged}: {flagged}");
        if (attachments > 0) parts.Add($"{Translator.FilteringOption_Files}: {attachments}");
        _summary.StringValue = string.Join(" · ", parts);
        _summary.Hidden = parts.Count == 0;
        AccessibilityLabel = _title.StringValue;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) ActionInvoked = null;
        base.Dispose(disposing);
    }
}

/// <summary>
/// Reader state "loading": a skeleton of the message (header and body placeholders with a shimmer
/// sweep) above a spinner and the download line, the whole group centred in the reading area.
/// </summary>
internal sealed class MailLoadingView : NSView
{
    private const double MaxContentWidth = 520;

    private readonly NSProgressIndicator _spinner;
    private readonly MailSkeletonView _skeleton;

    public MailLoadingView()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        _skeleton = new MailSkeletonView();
        _spinner = new NSProgressIndicator { Style = NSProgressIndicatorStyle.Spinning, ControlSize = NSControlSize.Small, TranslatesAutoresizingMaskIntoConstraints = false };
        // No translation key exists for this loading line yet.
        var text = WinoStyle.Label("Downloading message from the server…", NSFont.SystemFontOfSize(12), WinoStyle.SecondaryText);
        text.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        text.SetContentHuggingPriorityForOrientation(750, NSLayoutConstraintOrientation.Horizontal);
        var status = WinoLayout.HStack(8, _spinner, text);
        status.Alignment = NSLayoutAttribute.CenterY;
        // Hug the spinner and text so the row sits centred under the skeleton instead of leading.
        status.SetHuggingPriority(750, NSLayoutConstraintOrientation.Horizontal);

        var stack = WinoLayout.VStack(22, _skeleton, status);
        stack.Alignment = NSLayoutAttribute.CenterX;
        stack.TranslatesAutoresizingMaskIntoConstraints = false;
        AddSubview(stack);

        var preferredWidth = stack.WidthAnchor.ConstraintEqualTo((nfloat)MaxContentWidth);
        preferredWidth.Priority = 500;
        NSLayoutConstraint.ActivateConstraints(
        [
            stack.CenterXAnchor.ConstraintEqualTo(CenterXAnchor),
            stack.CenterYAnchor.ConstraintEqualTo(CenterYAnchor),
            stack.LeadingAnchor.ConstraintGreaterThanOrEqualTo(LeadingAnchor, 28),
            stack.TrailingAnchor.ConstraintLessThanOrEqualTo(TrailingAnchor, -28),
            stack.TopAnchor.ConstraintGreaterThanOrEqualTo(TopAnchor, 20),
            stack.WidthAnchor.ConstraintLessThanOrEqualTo((nfloat)MaxContentWidth),
            preferredWidth,
            _skeleton.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor),
            status.WidthAnchor.ConstraintLessThanOrEqualTo(stack.WidthAnchor),
            status.CenterXAnchor.ConstraintEqualTo(stack.CenterXAnchor)
        ]);
        AccessibilityElement = true;
        AccessibilityRole = NSAccessibilityRoles.ProgressIndicatorRole;
        AccessibilityLabel = "Downloading message from the server…";
    }

    public bool IsAnimating
    {
        set
        {
            if (value) _spinner.StartAnimation(null);
            else _spinner.StopAnimation(null);
            _skeleton.IsAnimating = value;
        }
    }
}

/// <summary>
/// Placeholder shapes for a message (subject, avatar with two sender lines, body paragraphs of
/// varied width) drawn as one masked layer, with a linear gradient sweeping across the shapes.
/// Colours follow the effective appearance; the sweep runs only while visible in a window.
/// </summary>
internal sealed class MailSkeletonView : NSView
{
    private const double ContentHeight = 236;
    private const string SweepKey = "wino.skeleton.sweep";

    private readonly CALayer _content = new();
    private readonly CAShapeLayer _mask = new();
    private readonly CAGradientLayer _sweep = new();
    private bool _isAnimating;
    private nfloat _laidOutWidth = -1;

    public MailSkeletonView()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        WantsLayer = true;
        _content.Mask = _mask;
        _sweep.StartPoint = new CGPoint(0, 0.5);
        _sweep.EndPoint = new CGPoint(1, 0.5);
        _sweep.Locations = [0.3, 0.5, 0.7];
        _content.AddSublayer(_sweep);
        Layer!.AddSublayer(_content);
        SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        AccessibilityElement = false;
        ApplyColors();
    }

    public override bool IsFlipped => true;

    public override CGSize IntrinsicContentSize => new(NSView.NoIntrinsicMetric, (nfloat)ContentHeight);

    public bool IsAnimating
    {
        get => _isAnimating;
        set { _isAnimating = value; UpdateAnimation(); }
    }

    public override void Layout()
    {
        base.Layout();
        var bounds = Bounds;
        if (bounds.Width == _laidOutWidth) return;
        _laidOutWidth = bounds.Width;
        CATransaction.Begin();
        CATransaction.DisableActions = true;
        _content.Frame = bounds;
        _mask.Frame = bounds;
        _mask.Path = BuildPath((double)bounds.Width);
        _sweep.Frame = new CGRect(0, 0, bounds.Width, bounds.Height);
        CATransaction.Commit();
        RestartAnimation();
    }

    /// <summary>The placeholder geometry in flipped (top-left) coordinates.</summary>
    private static CGPath BuildPath(double width)
    {
        var path = new CGPath();
        void Bar(double x, double y, double fraction, double height)
        {
            var w = Math.Max(0, (width - x) * fraction);
            if (w <= 0) return;
            path.AddRoundedRect(new CGRect(x, y, w, height), (nfloat)(height / 2), (nfloat)(height / 2));
        }

        // Subject.
        Bar(0, 0, 0.74, 18);
        // Sender: avatar circle and name/address lines.
        path.AddEllipseInRect(new CGRect(0, 34, 40, 40));
        Bar(52, 40, 0.42, 11);
        Bar(52, 60, 0.28, 9);
        // Body: two paragraphs with ragged line ends.
        double y = 100;
        foreach (var fraction in new[] { 0.97, 0.92, 0.95, 0.61 })
        {
            Bar(0, y, fraction, 10);
            y += 19;
        }
        y += 12;
        foreach (var fraction in new[] { 0.94, 0.86, 0.48 })
        {
            Bar(0, y, fraction, 10);
            y += 19;
        }
        return path;
    }

    private bool IsDark
        => EffectiveAppearance.FindBestMatch([NSAppearance.NameAqua, NSAppearance.NameDarkAqua]) == NSAppearance.NameDarkAqua;

    /// <summary>Base fill is the label colour at 8% for the appearance; the sweep is a soft highlight.</summary>
    private void ApplyColors()
    {
        bool dark = IsDark;
        var baseColor = dark ? WinoStyle.Hex(0xFFFFFF, 0.08) : WinoStyle.Hex(0x000000, 0.08);
        var highlight = dark ? WinoStyle.Hex(0xFFFFFF, 0.10) : WinoStyle.Hex(0xFFFFFF, 0.55);
        var clear = WinoStyle.Hex(0xFFFFFF, 0);
        CATransaction.Begin();
        CATransaction.DisableActions = true;
        _content.BackgroundColor = baseColor.CGColor;
        _sweep.Colors = [clear.CGColor, highlight.CGColor, clear.CGColor];
        CATransaction.Commit();
    }

    public override void ViewDidChangeEffectiveAppearance()
    {
        base.ViewDidChangeEffectiveAppearance();
        ApplyColors();
    }

    public override void ViewDidMoveToWindow()
    {
        base.ViewDidMoveToWindow();
        UpdateAnimation();
    }

    public override void ViewDidHide()
    {
        base.ViewDidHide();
        UpdateAnimation();
    }

    public override void ViewDidUnhide()
    {
        base.ViewDidUnhide();
        UpdateAnimation();
    }

    public override void RemoveFromSuperview()
    {
        StopAnimation();
        base.RemoveFromSuperview();
    }

    private bool ShouldAnimate => _isAnimating && Window is not null && !IsHiddenOrHasHiddenAncestor && Bounds.Width > 0;

    private void UpdateAnimation()
    {
        if (ShouldAnimate)
        {
            if (_sweep.AnimationForKey(SweepKey) is null) RestartAnimation();
        }
        else StopAnimation();
    }

    private void RestartAnimation()
    {
        StopAnimation();
        if (!ShouldAnimate) return;
        var width = (double)Bounds.Width;
        // The gradient band is one view wide and travels from fully left of the shapes to fully right.
        var animation = CABasicAnimation.FromKeyPath("transform.translation.x");
        animation.From = Foundation.NSNumber.FromDouble(-width);
        animation.To = Foundation.NSNumber.FromDouble(width);
        animation.Duration = 1.4;
        animation.RepeatCount = float.PositiveInfinity;
        animation.TimingFunction = CAMediaTimingFunction.FromName(CAMediaTimingFunction.Linear);
        animation.RemovedOnCompletion = false;
        _sweep.AddAnimation(animation, SweepKey);
    }

    private void StopAnimation() => _sweep.RemoveAnimation(SweepKey);
}

/// <summary>One reader command: a Wino glyph, a label, and what it runs.</summary>
internal sealed record MailReaderCommand(WinoIconGlyph Glyph, string Title, Action Run, bool IsEnabled = true);

/// <summary>
/// The reader command bar (Windows OperationCommandBar on MailRenderingPage): Reply, Reply all,
/// Forward, a divider, Archive, Delete, Move, Flag, Mark read, then More at the trailing edge.
/// Buttons show glyph and label at their intrinsic width. When the labelled bar does not fit, the
/// labels collapse; when the icons alone do not fit, trailing commands move into the More menu.
/// </summary>
internal sealed class MailReaderCommandBar : NSView
{
    private const double Spacing = 2;
    private const double DividerSpacing = 8;
    private const double MoreWidth = 30;

    private readonly NSStackView _stack;
    private readonly NSButton _moreButton;
    private readonly List<Slot> _slots = new();
    private Func<NSMenu>? _moreMenu;
    private bool _labelsHidden;
    private int _shown = -1;

    /// <summary>One arranged view: a command button, or a divider when <see cref="Button"/> is null.</summary>
    private sealed class Slot(NSView view, NSButton? button, MailReaderCommand? command)
    {
        public NSView View { get; } = view;
        public NSButton? Button { get; } = button;
        public MailReaderCommand? Command { get; set; } = command;
        public double LabelledWidth { get; set; }
        public double IconWidth { get; set; }
    }

    public MailReaderCommandBar()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        _stack = WinoLayout.HStack(Spacing);
        _stack.Distribution = NSStackViewDistribution.Fill;
        _stack.DetachesHiddenViews = true;
        _stack.EdgeInsets = new NSEdgeInsets(7, 10, 7, 10);
        _stack.SetClippingResistancePriority(200, NSLayoutConstraintOrientation.Horizontal);
        _moreButton = Button(WinoIconGlyph.More, Translator.More, ShowMore);
        _moreButton.ImagePosition = NSCellImagePosition.ImageOnly;
        WinoLayout.Size(_moreButton, MoreWidth, 30);
        var separator = new WinoSurfaceView { Fill = WinoStyle.ZoneStroke };
        AddSubview(_stack);
        AddSubview(separator);
        NSLayoutConstraint.ActivateConstraints(
        [
            _stack.LeadingAnchor.ConstraintEqualTo(LeadingAnchor),
            _stack.TrailingAnchor.ConstraintEqualTo(TrailingAnchor),
            _stack.TopAnchor.ConstraintEqualTo(TopAnchor),
            _stack.BottomAnchor.ConstraintEqualTo(BottomAnchor),
            separator.LeadingAnchor.ConstraintEqualTo(LeadingAnchor),
            separator.TrailingAnchor.ConstraintEqualTo(TrailingAnchor),
            separator.BottomAnchor.ConstraintEqualTo(BottomAnchor),
            separator.HeightAnchor.ConstraintEqualTo(1)
        ]);
        AccessibilityElement = true;
        AccessibilityRole = NSAccessibilityRoles.ToolbarRole;
    }

    private static NSButton Button(WinoIconGlyph glyph, string title, Action run)
    {
        var button = new NSButton
        {
            Bordered = true,
            BezelStyle = NSBezelStyle.Recessed,
            ShowsBorderOnlyWhileMouseInside = true,
            Title = title,
            Font = WinoStyle.Body,
            Image = WinoIcons.Image(glyph, 16, null, title),
            ImagePosition = NSCellImagePosition.ImageLeading,
            ImageHugsTitle = true,
            ContentTintColor = WinoStyle.PrimaryText,
            ToolTip = title,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        button.SetButtonType(NSButtonType.MomentaryPushIn);
        button.HeightAnchor.ConstraintEqualTo(30).Active = true;
        button.SetContentCompressionResistancePriority(990, NSLayoutConstraintOrientation.Horizontal);
        WinoAccessibility.Label(button, title);
        button.Activated += (_, _) => run();
        return button;
    }

    /// <summary>
    /// Replaces the primary commands; a null entry inserts the divider. The More menu is built on demand.
    /// The same glyph sequence keeps the existing buttons and updates their action, title and enabled state.
    /// </summary>
    public void SetCommands(IReadOnlyList<MailReaderCommand?> commands, Func<NSMenu>? moreMenu)
    {
        _moreMenu = moreMenu;
        if (!SameShape(commands)) Rebuild(commands);
        else
        {
            for (int i = 0; i < commands.Count; i++)
            {
                if (commands[i] is not { } command) continue;
                var slot = _slots[i];
                bool retitled = slot.Command!.Title != command.Title;
                slot.Command = command;
                slot.Button!.Enabled = command.IsEnabled;
                if (!retitled) continue;
                slot.Button.Image = WinoIcons.Image(command.Glyph, 16, null, command.Title);
                slot.Button.ToolTip = command.Title;
                WinoAccessibility.Label(slot.Button, command.Title);
                Measure(slot);
                _shown = -1;
            }
        }
        NeedsLayout = true;
    }

    private bool SameShape(IReadOnlyList<MailReaderCommand?> commands)
    {
        if (commands.Count != _slots.Count) return false;
        for (int i = 0; i < commands.Count; i++)
            if (commands[i]?.Glyph != _slots[i].Command?.Glyph) return false;
        return true;
    }

    private void Rebuild(IReadOnlyList<MailReaderCommand?> commands)
    {
        foreach (var view in _stack.ArrangedSubviews)
        {
            _stack.RemoveArrangedSubview(view);
            view.RemoveFromSuperview();
            if (!ReferenceEquals(view, _moreButton)) view.Dispose();
        }
        _slots.Clear();
        foreach (var command in commands)
        {
            if (command is null)
            {
                var divider = new WinoSurfaceView { Fill = WinoStyle.ZoneStroke };
                WinoLayout.Size(divider, 1, 18);
                _stack.AddArrangedSubview(divider);
                _stack.SetCustomSpacing((nfloat)DividerSpacing, divider);
                if (_stack.ArrangedSubviews.Length > 1) _stack.SetCustomSpacing((nfloat)DividerSpacing, _stack.ArrangedSubviews[^2]);
                _slots.Add(new Slot(divider, null, null));
                continue;
            }
            Slot? slot = null;
            // The slot's command is swapped in place by SetCommands, so the button runs the latest action.
            var button = Button(command.Glyph, command.Title, () => slot?.Command?.Run());
            button.Enabled = command.IsEnabled;
            slot = new Slot(button, button, command);
            Measure(slot);
            _slots.Add(slot);
            _stack.AddArrangedSubview(button);
        }
        var spacer = WinoLayout.Spacer();
        _stack.AddArrangedSubview(spacer);
        _stack.AddArrangedSubview(_moreButton);
        _shown = -1;
    }

    /// <summary>Records the intrinsic width with and without the label; <see cref="Apply"/> sets the shown form.</summary>
    private static void Measure(Slot slot)
    {
        var button = slot.Button!;
        button.ImagePosition = NSCellImagePosition.ImageLeading;
        button.Title = slot.Command!.Title;
        slot.LabelledWidth = (double)button.IntrinsicContentSize.Width;
        button.ImagePosition = NSCellImagePosition.ImageOnly;
        button.Title = string.Empty;
        slot.IconWidth = (double)button.IntrinsicContentSize.Width;
    }

    /// <summary>The bar width needed to show the first <paramref name="shown"/> commands, mirroring the stack spacing.</summary>
    private double RequiredWidth(bool labels, int shown, int total)
    {
        double width = (double)(_stack.EdgeInsets.Left + _stack.EdgeInsets.Right);
        int buttons = 0;
        int index = 0;
        for (; index < _slots.Count && buttons < shown; index++)
        {
            var slot = _slots[index];
            bool divider = slot.Button is null;
            if (index > 0) width += divider || _slots[index - 1].Button is null ? DividerSpacing : Spacing;
            width += divider ? 1 : labels ? slot.LabelledWidth : slot.IconWidth;
            if (!divider) buttons++;
        }
        // The view before a hidden divider keeps its custom spacing to the spacer; More follows the spacer.
        width += index > 0 && index < _slots.Count && _slots[index].Button is null ? DividerSpacing : Spacing;
        if (_moreMenu is not null || shown < total) width += Spacing + MoreWidth;
        return Math.Ceiling(width);
    }

    public override void Layout()
    {
        base.Layout();
        int total = _slots.Count(static slot => slot.Button is not null);
        if (total == 0) { _moreButton.Hidden = _moreMenu is null; return; }
        double available = (double)Bounds.Width;
        bool labels = RequiredWidth(true, total, total) <= available;
        int shown = total;
        if (!labels) while (shown > 0 && RequiredWidth(false, shown, total) > available) shown--;
        if (labels != _labelsHidden && shown == _shown) return;
        Apply(!labels, shown, total);
    }

    private void Apply(bool hideLabels, int shown, int total)
    {
        _labelsHidden = hideLabels;
        _shown = shown;
        int buttons = 0;
        foreach (var slot in _slots)
        {
            // A divider stays only while a command after it is still on the bar.
            slot.View.Hidden = buttons >= shown;
            if (slot.Button is not { } button) continue;
            button.ImagePosition = hideLabels ? NSCellImagePosition.ImageOnly : NSCellImagePosition.ImageLeading;
            button.Title = hideLabels ? string.Empty : slot.Command!.Title;
            buttons++;
        }
        _moreButton.Hidden = _moreMenu is null && shown == total;
    }

#if DEBUG
    /// <summary>Debug: one line per arranged view with its frame, title and enabled state.</summary>
    public string Dump()
        => $"bar {Bounds.Width}x{Bounds.Height} hidden={Hidden} shown={_shown} collapsed={_labelsHidden}" + Environment.NewLine + string.Join(Environment.NewLine, _stack.ArrangedSubviews.Select(static view =>
        {
            var line = $"{view.GetType().Name} {view.Frame.X:0},{view.Frame.Y:0} {view.Frame.Width:0}x{view.Frame.Height:0} hidden={view.Hidden} alpha={view.AlphaValue}";
            if (view is NSButton button) line += $" title='{button.Title}' enabled={button.Enabled} image={button.Image?.Size.Width ?? -1} pos={button.ImagePosition}";
            return line;
        }));
#endif

    /// <summary>Commands that did not fit come first in More, above the menu the owner builds.</summary>
    private void ShowMore()
    {
        var menu = _moreMenu?.Invoke() ?? new NSMenu { AutoEnablesItems = false };
        var hidden = _slots.Where(static slot => slot.Button is { Hidden: true }).Select(static slot => slot.Command!).ToList();
        if (hidden.Count == 0 && menu.Count == 0) return;
        if (hidden.Count > 0 && menu.Count > 0) menu.InsertItem(NSMenuItem.SeparatorItem, 0);
        for (int i = hidden.Count - 1; i >= 0; i--)
        {
            var command = hidden[i];
            menu.InsertItem(new NSMenuItem(command.Title, (_, _) => command.Run())
            {
                Enabled = command.IsEnabled,
                Image = WinoIcons.Image(command.Glyph, 16)
            }, 0);
        }
        menu.PopUpMenu(null, new CGPoint(0, _moreButton.Bounds.Height + 4), _moreButton);
    }
}
