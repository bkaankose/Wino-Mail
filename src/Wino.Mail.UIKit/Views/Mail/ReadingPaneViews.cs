using System.Windows.Input;
using CoreAnimation;
using CoreGraphics;
using Foundation;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.UIKit.Common;
using Wino.Mail.Controls.UIKit.Extras;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Mail;

/// <summary>
/// Reader state "no message selected" (Mac MailIdleView, design board "Reader states"): the Wino mail glyph, the title
/// and the hint line, or the account and mail-empty copy of the idle page with its action button. The iPad reader
/// column shows it; the hint mentions the hardware-keyboard shortcuts.
/// </summary>
public sealed class MailIdleView : UIView
{
    private readonly UILabel _title;
    private readonly UILabel _message;
    private readonly UIButton _action;

    public MailIdleView()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        var glyph = Illustration();
        _title = WinoStyle.Label(Translator.NoMailSelected, WinoStyle.Scaled(17, UIFontWeight.Semibold), WinoStyle.PrimaryText, 0);
        _title.TextAlignment = UITextAlignment.Center;
        _title.AccessibilityTraits = UIAccessibilityTrait.Header;
        _message = WinoStyle.Label(Translator.MacOS_Reader_KeyboardHint, WinoStyle.Description, WinoStyle.SecondaryText, 0);
        _message.TextAlignment = UITextAlignment.Center;
        var configuration = UIButtonConfiguration.FilledButtonConfiguration;
        configuration.CornerStyle = UIButtonConfigurationCornerStyle.Medium;
        _action = new UIButton(UIButtonType.System) { Configuration = configuration, Hidden = true, TranslatesAutoresizingMaskIntoConstraints = false };
        _action.PrimaryActionTriggered += (_, _) => ActionInvoked?.Invoke(this, EventArgs.Empty);
        var stack = WinoLayout.VStack(14, glyph, _title, _message, _action);
        stack.Alignment = UIStackViewAlignment.Center;
        AddSubview(stack);
        NSLayoutConstraint.ActivateConstraints(
        [
            stack.CenterXAnchor.ConstraintEqualTo(CenterXAnchor),
            stack.CenterYAnchor.ConstraintEqualTo(CenterYAnchor),
            stack.LeadingAnchor.ConstraintGreaterThanOrEqualTo(LeadingAnchor, 24),
            stack.TrailingAnchor.ConstraintLessThanOrEqualTo(TrailingAnchor, -24),
            _message.WidthAnchor.ConstraintLessThanOrEqualTo(320),
            _title.WidthAnchor.ConstraintLessThanOrEqualTo(360)
        ]);
    }

    /// <summary>The Wino logo from the app bundle (WinoLogo.png), or the Wino mail glyph when it is not bundled.</summary>
    private static UIView Illustration()
    {
        if (UIImage.FromBundle("WinoLogo") is { } logo)
        {
            var image = new UIImageView(logo) { ContentMode = UIViewContentMode.ScaleAspectFit, IsAccessibilityElement = false };
            return WinoLayout.Size(image, 88, 88);
        }
        var glyph = new WinoIconView(WinoIconGlyph.Mail, 64, WinoStyle.TertiaryText);
        return WinoLayout.Size(glyph, 64, 64);
    }

    public event EventHandler? ActionInvoked;

    /// <summary>Replaces the default copy, used for the account and mail-empty states of the idle page.</summary>
    public void SetContent(string title, string message, string? actionTitle)
    {
        _title.Text = title;
        _message.Text = message;
        _message.Hidden = string.IsNullOrEmpty(message);
        _action.Configuration = WithTitle(_action.Configuration, actionTitle);
        _action.Hidden = string.IsNullOrEmpty(actionTitle);
    }

    private static UIButtonConfiguration WithTitle(UIButtonConfiguration? configuration, string? title)
    {
        var copy = configuration ?? UIButtonConfiguration.FilledButtonConfiguration;
        copy.Title = title ?? string.Empty;
        return copy;
    }

#if DEBUG
    internal string Dump() => $"title='{_title.Text}' message='{_message.Text}' action='{(_action.Hidden ? string.Empty : _action.Configuration?.Title)}'";
#endif

    protected override void Dispose(bool disposing)
    {
        if (disposing) ActionInvoked = null;
        base.Dispose(disposing);
    }
}

/// <summary>The bulk actions of the multi-selection state; the list page maps them to its own commands.</summary>
public enum MailBulkAction
{
    Archive,
    Delete,
    Flag,
    ToggleRead,
    Move
}

/// <summary>
/// Reader state "multi-selection" (Mac MailMultiSelectionView, Windows MailListPage multi-selection pane): overlapping
/// avatars, the count, the summary and the bulk actions Archive, Delete, Flag, Mark read and Move, then Unselect All.
/// Flag and Mark read toggle, so their titles say what the tap will do. The iPad reader column shows it.
/// </summary>
public sealed class MailMultiSelectionView : UIView
{
    private readonly UIStackView _avatars;
    private readonly UILabel _title;
    private readonly UILabel _summary;
    private readonly UIButton _flag;
    private readonly UIButton _read;
    private readonly UIButton _unselectAll;

    public MailMultiSelectionView()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        _avatars = WinoLayout.HStack(-12);
        _title = WinoStyle.Label(string.Empty, WinoStyle.Scaled(20, UIFontWeight.Bold), WinoStyle.PrimaryText, 0);
        _title.TextAlignment = UITextAlignment.Center;
        _title.AccessibilityTraits = UIAccessibilityTrait.Header;
        _summary = WinoStyle.Label(string.Empty, WinoStyle.Description, WinoStyle.SecondaryText, 0);
        _summary.TextAlignment = UITextAlignment.Center;

        var archive = Action(WinoIconGlyph.Archive, Translator.MailOperation_Archive, MailBulkAction.Archive);
        var delete = Action(WinoIconGlyph.Delete, Translator.MailOperation_Delete, MailBulkAction.Delete);
        _flag = Action(WinoIconGlyph.Flag, Translator.MailOperation_Flag, MailBulkAction.Flag);
        _read = Action(WinoIconGlyph.MarkRead, Translator.MailOperation_MarkAsRead, MailBulkAction.ToggleRead);
        var move = Action(WinoIconGlyph.Move, $"{Translator.MailOperation_Move}…", MailBulkAction.Move);
        var first = Row(archive, delete);
        var second = Row(_flag, _read);
        var grid = WinoLayout.VStack(8, first, second, move);
        grid.Alignment = UIStackViewAlignment.Fill;
        WinoLayout.Size(grid, 260);

        var separator = new WinoSeparator();
        WinoLayout.Size(separator, 260);
        var link = UIButtonConfiguration.PlainButtonConfiguration;
        link.Title = Translator.Buttons_UnselectAll;
        _unselectAll = new UIButton(UIButtonType.System) { Configuration = link, TranslatesAutoresizingMaskIntoConstraints = false };
        _unselectAll.PrimaryActionTriggered += (_, _) => UnselectAllInvoked?.Invoke(this, EventArgs.Empty);
        WinoAccessibility.Label(_unselectAll, Translator.Buttons_UnselectAll);

        var stack = WinoLayout.VStack(16, _avatars, _title, _summary, grid, separator, _unselectAll);
        stack.SetCustomSpacing(12, grid);
        stack.SetCustomSpacing(8, separator);
        stack.Alignment = UIStackViewAlignment.Center;
        AddSubview(stack);
        NSLayoutConstraint.ActivateConstraints(
        [
            stack.CenterXAnchor.ConstraintEqualTo(CenterXAnchor),
            stack.CenterYAnchor.ConstraintEqualTo(CenterYAnchor),
            stack.LeadingAnchor.ConstraintGreaterThanOrEqualTo(LeadingAnchor, 24),
            stack.TrailingAnchor.ConstraintLessThanOrEqualTo(TrailingAnchor, -24)
        ]);
    }

    private static UIStackView Row(UIView leading, UIView trailing)
    {
        var row = WinoLayout.HStack(8, leading, trailing);
        row.Distribution = UIStackViewDistribution.FillEqually;
        row.Alignment = UIStackViewAlignment.Fill;
        return row;
    }

    private UIButton Action(WinoIconGlyph glyph, string title, MailBulkAction action)
    {
        var configuration = UIButtonConfiguration.GrayButtonConfiguration;
        configuration.Title = title;
        configuration.Image = WinoIcons.Image(glyph, 16, null, title);
        configuration.ImagePadding = 6;
        configuration.CornerStyle = UIButtonConfigurationCornerStyle.Medium;
        var button = new UIButton(UIButtonType.System) { Configuration = configuration, TranslatesAutoresizingMaskIntoConstraints = false };
        button.HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)WinoStyle.MinimumHitTarget).Active = true;
        button.PrimaryActionTriggered += (_, _) => ActionInvoked?.Invoke(button, action);
        return button;
    }

    /// <summary>Raised with the bulk action; the sender is the button, used as the Move popover anchor.</summary>
    public event EventHandler<MailBulkAction>? ActionInvoked;

    /// <summary>Unselect All was tapped.</summary>
    public event EventHandler? UnselectAllInvoked;

    public void Update(IReadOnlyList<(string Name, string Address)> senders, int count, int unread, int flagged, int attachments, Func<string?, UIImage?>? senderPicture = null)
    {
        foreach (var view in _avatars.ArrangedSubviews)
        {
            _avatars.RemoveArrangedSubview(view);
            view.RemoveFromSuperview();
            view.Dispose();
        }
        foreach (var (name, address) in senders.Take(3))
        {
            var picture = new WinoContactPicture(48) { Stroke = UIColor.SystemBackground, StrokeWidth = 2 };
            picture.SetIdentity(string.IsNullOrWhiteSpace(name) ? address : name, string.IsNullOrWhiteSpace(address) ? name : address);
            picture.Image = senderPicture?.Invoke(address);
            _avatars.AddArrangedSubview(picture);
        }
        _title.Text = string.Format(Translator.MailsSelected, count);
        var parts = new List<string>();
        if (unread > 0) parts.Add($"{Translator.FilteringOption_Unread}: {unread}");
        if (flagged > 0) parts.Add($"{Translator.FilteringOption_Flagged}: {flagged}");
        if (attachments > 0) parts.Add($"{Translator.FilteringOption_Files}: {attachments}");
        _summary.Text = string.Join(" · ", parts);
        _summary.Hidden = parts.Count == 0;
        AccessibilityLabel = _title.Text;
        // Flag and ToggleRead clear or set based on the whole selection.
        SetTitle(_flag, count > 0 && flagged == count ? Translator.MailOperation_ClearFlag : Translator.MailOperation_Flag);
        SetTitle(_read, count > 0 && unread == 0 ? Translator.MailOperation_MarkAsUnread : Translator.MailOperation_MarkAsRead);
    }

    private static void SetTitle(UIButton button, string title)
    {
        if (button.Configuration is not { } configuration || configuration.Title == title) return;
        configuration.Title = title;
        button.Configuration = configuration;
        WinoAccessibility.Label(button, title);
    }

    /// <summary>Debug: the bulk action titles.</summary>
    public string Dump() => $"title='{_title.Text}' flag='{_flag.Configuration?.Title}' read='{_read.Configuration?.Title}' hidden={Hidden}";

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ActionInvoked = null;
            UnselectAllInvoked = null;
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// Reader state "loading" (Mac MailLoadingView): a skeleton of the message with a shimmer sweep above a spinner and the
/// "Downloading message from the server" line, centred in the reading area.
/// </summary>
public sealed class MailLoadingView : UIView
{
    private const double MaxContentWidth = 520;

    private readonly UIActivityIndicatorView _spinner;
    private readonly MailSkeletonView _skeleton;

    public MailLoadingView()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        _skeleton = new MailSkeletonView();
        _spinner = new UIActivityIndicatorView(UIActivityIndicatorViewStyle.Medium) { HidesWhenStopped = false, TranslatesAutoresizingMaskIntoConstraints = false };
        var text = WinoStyle.Label(Translator.MacOS_Reader_DownloadingMessage, WinoStyle.Description, WinoStyle.SecondaryText, 0);
        text.SetContentCompressionResistancePriority(250, UILayoutConstraintAxis.Horizontal);
        var status = WinoLayout.HStack(8, _spinner, text);

        var stack = WinoLayout.VStack(22, _skeleton, status);
        stack.Alignment = UIStackViewAlignment.Center;
        AddSubview(stack);

        var preferredWidth = stack.WidthAnchor.ConstraintEqualTo((nfloat)MaxContentWidth);
        preferredWidth.Priority = 500;
        NSLayoutConstraint.ActivateConstraints(
        [
            stack.CenterXAnchor.ConstraintEqualTo(CenterXAnchor),
            stack.CenterYAnchor.ConstraintEqualTo(CenterYAnchor),
            stack.LeadingAnchor.ConstraintGreaterThanOrEqualTo(LeadingAnchor, 24),
            stack.TrailingAnchor.ConstraintLessThanOrEqualTo(TrailingAnchor, -24),
            stack.TopAnchor.ConstraintGreaterThanOrEqualTo(TopAnchor, 20),
            stack.WidthAnchor.ConstraintLessThanOrEqualTo((nfloat)MaxContentWidth),
            preferredWidth,
            _skeleton.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor),
            status.WidthAnchor.ConstraintLessThanOrEqualTo(stack.WidthAnchor)
        ]);
        IsAccessibilityElement = true;
        AccessibilityTraits = UIAccessibilityTrait.UpdatesFrequently;
        AccessibilityLabel = Translator.MacOS_Reader_DownloadingMessage;
    }

    public bool IsAnimating
    {
        set
        {
            if (value) _spinner.StartAnimating();
            else _spinner.StopAnimating();
            _skeleton.IsAnimating = value;
        }
    }
}

/// <summary>
/// Placeholder shapes for a message (Mac MailSkeletonView): subject, avatar with two sender lines and body paragraphs of
/// varied width as one masked layer, with a gradient sweeping across. The sweep runs only while visible in a window.
/// </summary>
public sealed class MailSkeletonView : UIView
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
        _content.Mask = _mask;
        _sweep.StartPoint = new CGPoint(0, 0.5);
        _sweep.EndPoint = new CGPoint(1, 0.5);
        _sweep.Locations = [NSNumber.FromDouble(0.3), NSNumber.FromDouble(0.5), NSNumber.FromDouble(0.7)];
        _content.AddSublayer(_sweep);
        Layer.AddSublayer(_content);
        SetContentHuggingPriority(1, UILayoutConstraintAxis.Horizontal);
        IsAccessibilityElement = false;
        WinoTraits.OnAppearanceChanged(this, static view => view.ApplyColors());
        ApplyColors();
    }

    public override CGSize IntrinsicContentSize => new(NoIntrinsicMetric, (nfloat)ContentHeight);

    public bool IsAnimating
    {
        get => _isAnimating;
        set { _isAnimating = value; UpdateAnimation(); }
    }

    public override void LayoutSubviews()
    {
        base.LayoutSubviews();
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

    /// <summary>The placeholder geometry (UIKit coordinates are top-left already).</summary>
    private static CGPath BuildPath(double width)
    {
        var path = new CGPath();
        void Bar(double x, double y, double fraction, double height)
        {
            var w = Math.Max(0, (width - x) * fraction);
            if (w <= 0) return;
            path.AddRoundedRect(new CGRect(x, y, w, height), (nfloat)(height / 2), (nfloat)(height / 2));
        }

        Bar(0, 0, 0.74, 18);
        path.AddEllipseInRect(new CGRect(0, 34, 40, 40));
        Bar(52, 40, 0.42, 11);
        Bar(52, 60, 0.28, 9);
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

    private void ApplyColors()
    {
        bool dark = WinoTraits.IsDark(TraitCollection);
        var baseColor = dark ? WinoStyle.Hex(0xFFFFFF, 0.08) : WinoStyle.Hex(0x000000, 0.08);
        var highlight = dark ? WinoStyle.Hex(0xFFFFFF, 0.10) : WinoStyle.Hex(0xFFFFFF, 0.55);
        var clear = WinoStyle.Hex(0xFFFFFF, 0);
        CATransaction.Begin();
        CATransaction.DisableActions = true;
        _content.BackgroundColor = baseColor.CGColor;
        _sweep.Colors = [clear.CGColor, highlight.CGColor, clear.CGColor];
        CATransaction.Commit();
    }

    public override void MovedToWindow()
    {
        base.MovedToWindow();
        UpdateAnimation();
    }

    public override bool Hidden
    {
        get => base.Hidden;
        set
        {
            base.Hidden = value;
            UpdateAnimation();
        }
    }

    public override void RemoveFromSuperview()
    {
        StopAnimation();
        base.RemoveFromSuperview();
    }

    private bool ShouldAnimate => _isAnimating && Window is not null && !Hidden && Bounds.Width > 0;

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
        var animation = CABasicAnimation.FromKeyPath("transform.translation.x");
        animation.From = NSNumber.FromDouble(-width);
        animation.To = NSNumber.FromDouble(width);
        animation.Duration = 1.4;
        animation.RepeatCount = float.PositiveInfinity;
        animation.TimingFunction = CAMediaTimingFunction.FromName(CAMediaTimingFunction.Linear);
        animation.RemovedOnCompletion = false;
        _sweep.AddAnimation(animation, SweepKey);
    }

    private void StopAnimation() => _sweep.RemoveAnimation(SweepKey);
}

/// <summary>
/// One reader command (Mac MailReaderCommand): the operation, a Wino glyph, a label and what it runs. It is an
/// <see cref="ICommand"/> whose CanExecute is <see cref="IsEnabled"/>, so the phone shell's bottom toolbar can bind it
/// like the page's own command bar. A new command list replaces it (<see cref="IMailReaderCommands.CommandsChanged"/>),
/// so <see cref="CanExecuteChanged"/> never fires.
/// </summary>
public sealed class MailReaderCommand(MailOperation operation, WinoIconGlyph glyph, string title, Action run, bool isEnabled = true, Action<UIView>? runFrom = null) : ICommand
{
    public MailOperation Operation { get; } = operation;
    public WinoIconGlyph Glyph { get; } = glyph;
    public string Title { get; } = title;
    public bool IsEnabled { get; } = isEnabled;

    /// <summary>Runs the command; <paramref name="anchor"/> is the tapped view, for commands that present from it.</summary>
    public void Invoke(UIView? anchor)
    {
        if (!IsEnabled) return;
        if (runFrom is not null && anchor is not null) runFrom(anchor);
        else run();
    }

    /// <summary>A menu action for this command (More menus, the phone toolbar's Reply menu).</summary>
    public UIAction CreateAction(UIView? anchor)
    {
        var action = UIAction.Create(Title, WinoIcons.Image(Glyph, 18, null, Title), null, _ => Invoke(anchor));
        if (!IsEnabled) action.Attributes = UIMenuElementAttributes.Disabled;
        if (Operation is MailOperation.SoftDelete or MailOperation.HardDelete) action.Attributes |= UIMenuElementAttributes.Destructive;
        return action;
    }

    bool ICommand.CanExecute(object? parameter) => IsEnabled;

    void ICommand.Execute(object? parameter) => Invoke(parameter as UIView);

    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }
}

/// <summary>
/// The reader's command model, implemented by <see cref="MailRenderingPageViewController"/>. The iPad reader shows it in
/// its own <see cref="MailReaderCommandBar"/>; on iPhone the page hides that bar (<see cref="ShowsCommandBar"/>) and the
/// phone shell builds its bottom toolbar from the same commands. All members are main-thread only.
/// </summary>
public interface IMailReaderCommands
{
    /// <summary>
    /// The primary commands in Windows order (Reply, Reply all, Forward | Archive, Delete, Move, Flag, Mark read); a null
    /// entry is a group divider. Disabled defaults while nothing is loaded.
    /// </summary>
    IReadOnlyList<MailReaderCommand?> PrimaryCommands { get; }

    /// <summary>The overflow commands (the More menu: every other operation, Reader View and the theme toggle).</summary>
    UIMenu CreateMoreMenu(UIView? anchor);

    /// <summary>Raised after <see cref="PrimaryCommands"/> or the More content changed.</summary>
    event EventHandler? CommandsChanged;

    /// <summary>Whether the page shows its own command bar. Defaults to false on iPhone and true on iPad.</summary>
    bool ShowsCommandBar { get; set; }
}

/// <summary>
/// The reader command bar (Mac MailReaderCommandBar, Windows OperationCommandBar): Reply, Reply all, Forward, a divider,
/// Archive, Delete, Move, Flag, Mark read, then More at the trailing edge. Buttons show glyph and label; when the
/// labelled bar does not fit the labels collapse, and when the icons alone do not fit the trailing commands move into
/// More.
/// </summary>
public sealed class MailReaderCommandBar : UIView
{
    private const double Spacing = 2;
    private const double DividerSpacing = 8;
    private const double ButtonHeight = 40;
    private const double MoreWidth = 44;

    private readonly UIStackView _stack;
    private readonly UIButton _moreButton;
    private readonly List<Slot> _slots = new();
    private Func<UIView?, UIMenu>? _moreMenu;
    private bool _labelsHidden;
    private int _shown = -1;

    private sealed class Slot(UIView view, UIButton? button, MailReaderCommand? command)
    {
        public UIView View { get; } = view;
        public UIButton? Button { get; } = button;
        public MailReaderCommand? Command { get; set; } = command;
        public double LabelledWidth { get; set; }
        public double IconWidth { get; set; }
    }

    public MailReaderCommandBar()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        _stack = WinoLayout.Insets(WinoLayout.HStack(Spacing), 4, 8, 4, 8);
        _stack.Distribution = UIStackViewDistribution.Fill;
        _moreButton = CreateButton(WinoIconGlyph.More, Translator.More);
        _moreButton.ShowsMenuAsPrimaryAction = true;
        _moreButton.Menu = UIMenu.Create([UIDeferredMenuElement.CreateUncached(completion =>
        {
            completion(BuildMoreElements());
        })]);
        WinoLayout.Size(_moreButton, MoreWidth, ButtonHeight);
        var separator = new WinoSeparator();
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
            separator.BottomAnchor.ConstraintEqualTo(BottomAnchor)
        ]);
        AccessibilityTraits = UIAccessibilityTrait.None;
        ShouldGroupAccessibilityChildren = true;
    }

    private static UIButton CreateButton(WinoIconGlyph glyph, string title)
    {
        var configuration = UIButtonConfiguration.PlainButtonConfiguration;
        configuration.Image = WinoIcons.Image(glyph, 18, null, title);
        configuration.ImagePadding = 6;
        configuration.BaseForegroundColor = WinoStyle.PrimaryText;
        configuration.ContentInsets = new NSDirectionalEdgeInsets(6, 8, 6, 8);
        configuration.TitleTextAttributesTransformer = attributes =>
        {
            attributes[UIStringAttributeKey.Font] = WinoStyle.Fixed(15);
            return attributes;
        };
        var button = new UIButton(UIButtonType.System) { Configuration = configuration, TranslatesAutoresizingMaskIntoConstraints = false };
        button.HeightAnchor.ConstraintEqualTo((nfloat)ButtonHeight).Active = true;
        button.SetContentCompressionResistancePriority(990, UILayoutConstraintAxis.Horizontal);
        button.PointerInteractionEnabled = true;
        WinoAccessibility.Label(button, title);
        return button;
    }

    /// <summary>
    /// Replaces the primary commands; a null entry inserts the divider. The More menu is built on demand. The same glyph
    /// sequence keeps the existing buttons and updates their command and enabled state.
    /// </summary>
    public void SetCommands(IReadOnlyList<MailReaderCommand?> commands, Func<UIView?, UIMenu>? moreMenu)
    {
        _moreMenu = moreMenu;
        if (!SameShape(commands)) Rebuild(commands);
        else
        {
            for (int i = 0; i < commands.Count; i++)
            {
                if (commands[i] is not { } command || _slots[i].Button is not { } button) continue;
                var previous = _slots[i].Command;
                _slots[i].Command = command;
                button.Enabled = command.IsEnabled;
                if (previous?.Title == command.Title) continue;
                WinoAccessibility.Label(button, command.Title);
                Measure(_slots[i]);
                _shown = -1;
            }
        }
        SetNeedsLayout();
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
                WinoLayout.Size(divider, 1, 22);
                _stack.AddArrangedSubview(divider);
                _stack.SetCustomSpacing((nfloat)DividerSpacing, divider);
                if (_stack.ArrangedSubviews.Length > 1) _stack.SetCustomSpacing((nfloat)DividerSpacing, _stack.ArrangedSubviews[^2]);
                _slots.Add(new Slot(divider, null, null));
                continue;
            }
            var button = CreateButton(command.Glyph, command.Title);
            button.Enabled = command.IsEnabled;
            var slot = new Slot(button, button, command);
            // The slot's command is swapped in place by SetCommands, so the button runs the latest action.
            button.PrimaryActionTriggered += (_, _) => slot.Command?.Invoke(button);
            Measure(slot);
            _slots.Add(slot);
            _stack.AddArrangedSubview(button);
        }
        _stack.AddArrangedSubview(WinoLayout.Spacer());
        _stack.AddArrangedSubview(_moreButton);
        _shown = -1;
    }

    /// <summary>Records the fitting width with and without the label; <see cref="Apply"/> sets the shown form.</summary>
    private static void Measure(Slot slot)
    {
        if (slot.Button is not { } button || slot.Command is not { } command) return;
        SetTitle(button, command.Title);
        slot.LabelledWidth = (double)button.SizeThatFits(new CoreGraphics.CGSize(1000, ButtonHeight)).Width;
        SetTitle(button, null);
        slot.IconWidth = (double)button.SizeThatFits(new CoreGraphics.CGSize(1000, ButtonHeight)).Width;
    }

    private static void SetTitle(UIButton button, string? title)
    {
        if (button.Configuration is not { } configuration) return;
        configuration.Title = title;
        button.Configuration = configuration;
    }

    private double RequiredWidth(bool labels, int shown, int total)
    {
        double width = 16;
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
        width += Spacing + MoreWidth;
        return Math.Ceiling(width);
    }

    public override void LayoutSubviews()
    {
        base.LayoutSubviews();
        int total = _slots.Count(static slot => slot.Button is not null);
        if (total == 0) return;
        double available = (double)Bounds.Width;
        if (available <= 0) return;
        bool labels = RequiredWidth(true, total, total) <= available;
        int shown = total;
        if (!labels) while (shown > 0 && RequiredWidth(false, shown, total) > available) shown--;
        if (labels != _labelsHidden && shown == _shown) return;
        Apply(!labels, shown);
    }

    private void Apply(bool hideLabels, int shown)
    {
        _labelsHidden = hideLabels;
        _shown = shown;
        int buttons = 0;
        foreach (var slot in _slots)
        {
            // A divider stays only while a command after it is still on the bar.
            slot.View.Hidden = buttons >= shown;
            if (slot.Button is not { } button) continue;
            SetTitle(button, hideLabels ? null : slot.Command?.Title);
            button.InvalidateIntrinsicContentSize();
            buttons++;
        }
        // This runs inside the bar's layout pass, after the stack was laid out with the previous titles.
        _stack.SetNeedsLayout();
        _stack.LayoutIfNeeded();
    }

    /// <summary>Commands that did not fit come first in More, above the menu the owner builds.</summary>
    private UIMenuElement[] BuildMoreElements()
    {
        var elements = new List<UIMenuElement>();
        var hidden = _slots.Where(static slot => slot.Button is { Hidden: true }).Select(static slot => slot.Command).OfType<MailReaderCommand>().ToArray();
        if (hidden.Length > 0)
            elements.Add(UIMenu.Create(string.Empty, null, UIMenuIdentifier.None, UIMenuOptions.DisplayInline, hidden.Select(command => (UIMenuElement)command.CreateAction(_moreButton)).ToArray()));
        if (_moreMenu?.Invoke(_moreButton) is { } menu) elements.AddRange(menu.Children);
        return elements.ToArray();
    }

#if DEBUG
    /// <summary>Debug: one line per arranged view with its frame, title and enabled state.</summary>
    public string Dump()
        => $"bar {Bounds.Width}x{Bounds.Height} hidden={Hidden} shown={_shown} collapsed={_labelsHidden} | " + string.Join(" | ", _slots.Select(static slot =>
            slot.Button is { } button ? $"{slot.Command?.Title} enabled={button.Enabled} hidden={button.Hidden}" : "divider"));
#endif
}

/// <summary>
/// The status row under the reader's sender row (Mac MailReaderStatusRow): the Unsubscribe link, the S/MIME signature
/// button with its valid (success) or invalid (caution) badge, and the encrypted lock. Hidden when there are none.
/// </summary>
public sealed class MailReaderStatusRow : UIView
{
    private readonly UIButton _unsubscribe;
    private readonly WinoPressableView _signature;
    private readonly WinoSurfaceView _signatureBadge;
    private readonly WinoIconView _encrypted;
    private bool _signaturesValid;

    public MailReaderStatusRow()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        var configuration = UIButtonConfiguration.PlainButtonConfiguration;
        configuration.Title = Translator.Unsubscribe;
        configuration.Image = WinoIcons.Image(WinoIconGlyph.PersonDelete, 14, null, Translator.Unsubscribe);
        configuration.ImagePadding = 4;
        configuration.ContentInsets = new NSDirectionalEdgeInsets(4, 0, 4, 0);
        configuration.TitleTextAttributesTransformer = attributes =>
        {
            attributes[UIStringAttributeKey.Font] = WinoStyle.Caption;
            return attributes;
        };
        _unsubscribe = new UIButton(UIButtonType.System) { Configuration = configuration, Hidden = true, TranslatesAutoresizingMaskIntoConstraints = false };
        _unsubscribe.PrimaryActionTriggered += (_, _) => UnsubscribeInvoked?.Invoke(this, EventArgs.Empty);

        var certificate = new WinoIconView(WinoIconGlyph.Certificate, 18);
        WinoLayout.Size(certificate, 18, 18);
        _signatureBadge = new WinoSurfaceView { CornerRadius = 4, Fill = WinoStyle.Success };
        WinoLayout.Size(_signatureBadge, 8, 8);
        _signature = new WinoPressableView { Hidden = true };
        WinoLayout.Size(_signature, 28, 28);
        _signature.AddSubview(certificate);
        _signature.AddSubview(_signatureBadge);
        NSLayoutConstraint.ActivateConstraints(
        [
            certificate.CenterXAnchor.ConstraintEqualTo(_signature.CenterXAnchor),
            certificate.CenterYAnchor.ConstraintEqualTo(_signature.CenterYAnchor),
            _signatureBadge.TrailingAnchor.ConstraintEqualTo(_signature.TrailingAnchor, -2),
            _signatureBadge.TopAnchor.ConstraintEqualTo(_signature.TopAnchor, 3)
        ]);
        _signature.Clicked += (_, _) => SignatureInvoked?.Invoke(this, EventArgs.Empty);
        WinoAccessibility.Label(_signature, Translator.SmimeSignedTooltip);

        _encrypted = new WinoIconView(WinoIconGlyph.LockClosed, 18) { Hidden = true };
        WinoLayout.Size(_encrypted, 18, 18);
        WinoAccessibility.Element(_encrypted, UIAccessibilityTrait.Image, Translator.SmimeEncryptedTooltip);

        var stack = WinoLayout.HStack(10, _unsubscribe, _signature, _encrypted);
        WinoLayout.Fill(stack, this);
        Hidden = true;
    }

    public event EventHandler? UnsubscribeInvoked;

    public event EventHandler? SignatureInvoked;

    public void Update(bool canUnsubscribe, bool isSigned, bool signaturesValid, bool isEncrypted)
    {
        _unsubscribe.Hidden = !canUnsubscribe;
        _signature.Hidden = !isSigned;
        _signaturesValid = signaturesValid;
        _signatureBadge.Fill = signaturesValid ? WinoStyle.Success : WinoStyle.Caution;
        _encrypted.Hidden = !isEncrypted;
        Hidden = !canUnsubscribe && !isSigned && !isEncrypted;
    }

    public string Dump()
        => $"row={(Hidden ? "hidden" : "visible")} unsubscribe={!_unsubscribe.Hidden} signed={!_signature.Hidden} " +
           $"badge={(_signaturesValid ? "valid" : "invalid")} encrypted={!_encrypted.Hidden}";

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            UnsubscribeInvoked = null;
            SignatureInvoked = null;
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// A borderless text link (recipient tokens, "{0} more"): accent title in the tint colour, which follows the Wino accent
/// through the window tint, and the Link accessibility trait. A long press shows <see cref="UIButton.Menu"/> when set.
/// </summary>
public sealed class MailLinkButton : UIButton
{
    public MailLinkButton(string text, UIFont? font = null)
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        var configuration = UIButtonConfiguration.PlainButtonConfiguration;
        configuration.ContentInsets = new NSDirectionalEdgeInsets(2, 0, 2, 0);
        var resolved = font ?? WinoStyle.Description;
        configuration.TitleTextAttributesTransformer = attributes =>
        {
            attributes[UIStringAttributeKey.Font] = resolved;
            return attributes;
        };
        configuration.TitleLineBreakMode = UILineBreakMode.TailTruncation;
        Configuration = configuration;
        Text = text;
        AccessibilityTraits = UIAccessibilityTrait.Link;
        PointerInteractionEnabled = true;
    }

    public string Text
    {
        get => Configuration?.Title ?? string.Empty;
        set
        {
            if (Configuration is not { } configuration) return;
            configuration.Title = value;
            Configuration = configuration;
        }
    }
}
