using CoreFoundation;
using Foundation;
using UIKit;
using Wino.Core.Domain.Enums;
using Wino.Presentation.UIKit;

namespace Wino.Mail.Controls.UIKit.Settings;

/// <summary>
/// The one generic detail page every settings push opens (SettingsControls board, 15 Oct 2026). It is built from the
/// card or expander that pushed it, never per setting: the navigation title is the card's header, choices are an option
/// list with an accent checkmark that selects through the control's own binding, fields, steppers, sliders and pickers
/// sit in a single-cell group, vertical custom content is hosted in a group, and the description is the group footer.
/// An expander's items become rows by the same rules as a settings page (a switch stays a switch, a stepper is drawn on
/// the row, a choice is an option section with the item's title as its header).
/// </summary>
public sealed class WinoSettingDetailViewController : UIViewController
{
    /// <summary>Phone padding inside the zone and the readable width (the settings page values).</summary>
    private const double SidePadding = 10;
    private const double ContentMaxWidth = 836;

    private readonly Action<WinoSettingDetailViewController> _build;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly List<Action> _appearing = [];
    private readonly List<Action> _disappearing = [];
    private UIStackView? _page;
    private UIResponder? _focus;
    private WinoSettingsGroup? _firstGroup;

    private WinoSettingDetailViewController(string title, Action<WinoSettingDetailViewController> build)
    {
        Title = title;
        _build = build;
    }

    /// <summary>Runs when a detail page appears (the Settings sheet installs its chrome).</summary>
    public static Action<UIViewController>? Appearing { get; set; }

    public static WinoSettingDetailViewController ForCard(WinoSettingsCard card)
        => new(card.Header, page => page.BuildCard(card));

    public static WinoSettingDetailViewController ForExpander(WinoSettingsExpander expander)
        => new(expander.HeaderCard.Header, page => page.BuildExpander(expander));

    /// <summary>Pushes <paramref name="detail"/> on the navigation stack of the page that shows <paramref name="source"/>.</summary>
    public static void Push(UIView source, WinoSettingDetailViewController detail)
    {
        UIResponder? responder = source;
        while (responder is not null and not UIViewController) responder = responder.NextResponder;
        if (responder is UIViewController { NavigationController: { } navigation }) navigation.PushViewController(detail, true);
    }

    private UIStackView Page => _page ?? throw new InvalidOperationException("The detail page has not been loaded.");

    public override void LoadView()
    {
        _page = WinoLayout.VStack(WinoSettingsStyle.GroupSpacing);
        _page.Alignment = UIStackViewAlignment.Fill;
        View = CreateScrollRoot(_page);
        NavigationItem.LargeTitleDisplayMode = UINavigationItemLargeTitleDisplayMode.Never;
        _build(this);
    }

    public override void ViewWillAppear(bool animated)
    {
        base.ViewWillAppear(animated);
        Appearing?.Invoke(this);
        foreach (var action in _appearing) action();
    }

    public override void ViewDidAppear(bool animated)
    {
        base.ViewDidAppear(animated);
        _focus?.BecomeFirstResponder();
        _focus = null;
    }

    public override void ViewWillDisappear(bool animated)
    {
        base.ViewWillDisappear(animated);
        View?.EndEditing(true);
        foreach (var action in _disappearing) action();
    }

    public override void ViewDidDisappear(bool animated)
    {
        base.ViewDidDisappear(animated);
        if (!IsMovingFromParentViewController && NavigationController is not null) return;
        foreach (var subscription in _subscriptions) subscription.Dispose();
        _subscriptions.Clear();
    }

    private void BuildCard(WinoSettingsCard card)
    {
        _disappearing.Add(card.RefreshValue);
        var content = card.Content;
        var bottom = card.DetailBottomContent;
        var footer = card.DescriptionText;
        var kind = WinoSettingsRowContent.Classify(content, bottom);
        if (kind == WinoSettingsRowKind.Value && content is not null)
        {
            var control = WinoSettingsRowContent.ValueControl(content);
            if (control is not null && WinoSettingsRowContent.IsChoiceControl(control)) AddOptions(null, control, footer, card);
            else AddControlCell(card.Header, content, footer, card);
            return;
        }

        if (content is not null && bottom is not null)
        {
            AddControlCell(card.Header, content, null, card);
            AddHosted(bottom, footer, null, card);
        }
        else if ((content ?? bottom) is { } hosted) AddHosted(hosted, footer, null, card);
        else AddFooterOnly(footer);
    }

    private void BuildExpander(WinoSettingsExpander expander)
    {
        var header = expander.HeaderCard;
        _disappearing.Add(header.RefreshValue);
        var footer = header.DescriptionText;

        // A value in the expander header (a pop-up beside its title) leads the page; a header switch stays on the parent row.
        if (header.Content is { } headerContent && WinoSettingsRowContent.Classify(headerContent, null) == WinoSettingsRowKind.Value)
        {
            var control = WinoSettingsRowContent.ValueControl(headerContent);
            if (control is not null && WinoSettingsRowContent.IsChoiceControl(control)) AddOptions(null, control, footer, header);
            else AddControlCell(header.Header, headerContent, footer, header);
            footer = null;
        }

        WinoSettingsGroup? current = null;
        foreach (var entry in expander.Entries)
        {
            switch (entry.View)
            {
                case WinoSettingsCard card:
                {
                    var kind = WinoSettingsRowContent.Classify(card.Content, card.DetailBottomContent);
                    var control = card.Content is { } content ? WinoSettingsRowContent.ValueControl(content) : null;
                    if (kind == WinoSettingsRowKind.Value && !card.IsClickable && WinoSettingsRowContent.IsChoiceControl(control) && control is not null)
                    {
                        current = null;
                        // A choice titled like the page needs no section header of its own.
                        var sectionHeader = string.Equals(card.Header, Title, StringComparison.CurrentCultureIgnoreCase) ? null : card.Header;
                        AddOptions(sectionHeader, control, card.DescriptionText, card, followsVisibility: true);
                        break;
                    }
                    card.ShowsValueControlInline = true;
                    current ??= NewGroup();
                    current.Add(card);
                    if (card.RowKind is not (WinoSettingsRowKind.Static or WinoSettingsRowKind.Inline) && card.DescriptionText is { } description)
                    {
                        current.Footer = description;
                        current = null;
                    }
                    break;
                }
                case WinoSettingsExpander nested:
                    current ??= NewGroup();
                    current.Add(nested);
                    break;
                default:
                    current = null;
                    AddHosted(entry.View, null, entry.Insets, null);
                    break;
            }
        }

        if (string.IsNullOrEmpty(footer)) return;
        if (_firstGroup is { } first && string.IsNullOrEmpty(first.Footer)) first.Footer = footer;
        else Page.InsertArrangedSubview(Note(footer), 0);
    }

    private WinoSettingsGroup NewGroup()
    {
        var group = new WinoSettingsGroup();
        _firstGroup ??= group;
        Page.AddArrangedSubview(group);
        return group;
    }

    /// <summary>One inset group with a row per option; the selected row carries the accent checkmark.</summary>
    private void AddOptions(string? header, UIView control, string? footer, WinoSettingsCard owner, bool followsVisibility = false)
    {
        var group = new WinoSettingsGroup(header, footer);
        _firstGroup ??= group;
        var list = new OptionList(control, owner.RefreshValue);
        group.Add(list);
        Page.AddArrangedSubview(group);
        _appearing.Add(list.Reload);
        Mirror(owner, group, list, followsVisibility);
    }

    /// <summary>A single-cell group with the control: compact controls trail the title, fields and sliders fill the cell.</summary>
    private void AddControlCell(string title, UIView content, string? footer, WinoSettingsCard owner)
    {
        var group = new WinoSettingsGroup(null, footer);
        _firstGroup ??= group;
        var cell = Surface();
        content.RemoveFromSuperview();
        content.TranslatesAutoresizingMaskIntoConstraints = false;
        var control = WinoSettingsRowContent.ValueControl(content);
        if (control is UITextField field)
        {
            field.BorderStyle = UITextBorderStyle.None;
            field.TextAlignment = UITextAlignment.Natural;
            field.Font = WinoSettingsStyle.CardTitle;
            field.ClearButtonMode = UITextFieldViewMode.WhileEditing;
            if (string.IsNullOrEmpty(field.Placeholder)) field.Placeholder = title;
            if (string.IsNullOrEmpty(field.AccessibilityLabel)) field.AccessibilityLabel = title;
            WinoLayout.Fill(content, cell, 8, WinoSettingsStyle.CardPadding, 8, WinoSettingsStyle.CardPadding);
            cell.HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)WinoSettingsStyle.RowMinHeight).Active = true;
            field.ReturnKeyType = UIReturnKeyType.Done;
            field.ShouldReturn = target => { target.ResignFirstResponder(); return true; };
            _focus = field;
        }
        else if (control is UIStepper or UIDatePicker or UISwitch || content is WinoLabeledSwitch)
        {
            if (control is UIDatePicker picker && picker.PreferredDatePickerStyle != UIDatePickerStyle.Compact)
                picker.PreferredDatePickerStyle = UIDatePickerStyle.Compact;
            var label = WinoStyle.Label(title, WinoSettingsStyle.CardTitle, WinoStyle.PrimaryText, 2);
            WinoLayout.Priorities(label, UILayoutConstraintAxis.Horizontal, 250, 740);
            WinoLayout.Priorities(content, UILayoutConstraintAxis.Horizontal, 750, 750);
            var row = WinoLayout.HStack(12, label, content);
            row.Alignment = UIStackViewAlignment.Center;
            WinoLayout.Fill(row, cell, WinoSettingsStyle.RowVerticalPadding, WinoSettingsStyle.CardPadding, WinoSettingsStyle.RowVerticalPadding, WinoSettingsStyle.CardPadding);
            cell.HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)WinoSettingsStyle.RowMinHeight).Active = true;
        }
        else
        {
            WinoLayout.Fill(content, cell, 12, WinoSettingsStyle.CardPadding, 12, WinoSettingsStyle.CardPadding);
            cell.HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)WinoSettingsStyle.RowMinHeight).Active = true;
        }
        if (control is UIControl valueControl)
        {
            EventHandler changed = (_, _) => owner.RefreshValue();
            valueControl.AddTarget(changed, UIControlEvent.ValueChanged | UIControlEvent.EditingChanged | UIControlEvent.EditingDidEnd);
            _subscriptions.Add(new SettingsDisposable(() => valueControl.RemoveTarget(changed, UIControlEvent.ValueChanged | UIControlEvent.EditingChanged | UIControlEvent.EditingDidEnd)));
        }
        group.Add(cell);
        Page.AddArrangedSubview(group);
        Mirror(owner, group, cell, false);
    }

    /// <summary>Custom content (tiles, previews, swatches, lists) in a group of its own.</summary>
    private void AddHosted(UIView view, string? footer, UIEdgeInsets? insets, WinoSettingsCard? owner)
    {
        var group = new WinoSettingsGroup(null, footer);
        _firstGroup ??= group;
        var cell = Surface();
        view.RemoveFromSuperview();
        view.TranslatesAutoresizingMaskIntoConstraints = false;
        var edge = insets ?? new UIEdgeInsets(12, (nfloat)WinoSettingsStyle.CardPadding, 12, (nfloat)WinoSettingsStyle.CardPadding);
        WinoLayout.Fill(view, cell, edge.Top, edge.Left, edge.Bottom, edge.Right);
        group.Add(cell);
        Page.AddArrangedSubview(group);
        // A hidden item hides its group too.
        var observer = view.AddObserver("hidden", NSKeyValueObservingOptions.Initial | NSKeyValueObservingOptions.New, _ => group.Hidden = view.Hidden);
        _subscriptions.Add(observer);
        if (owner is not null) Mirror(owner, group, cell, false);
    }

    private void AddFooterOnly(string? footer)
    {
        if (!string.IsNullOrEmpty(footer)) Page.AddArrangedSubview(Note(footer));
    }

    private static UIView Note(string text)
    {
        var host = new UIView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(WinoStyle.Label(text, WinoSettingsStyle.CardDescription, WinoStyle.SecondaryText, 0), host, 0, WinoSettingsStyle.CardPadding, 0, WinoSettingsStyle.CardPadding);
        return host;
    }

    private static WinoSurfaceView Surface() => new()
    {
        Fill = WinoSettingsStyle.GroupFill,
        Stroke = WinoSettingsStyle.CardStroke,
        CornerRadius = WinoSettingsStyle.CardRadius,
        ClipsToBounds = true,
        TranslatesAutoresizingMaskIntoConstraints = false
    };

    /// <summary>The section follows its card: hidden with it, dimmed and not selectable while it is disabled.</summary>
    private void Mirror(WinoSettingsCard owner, UIView section, UIView interactive, bool followsVisibility)
    {
        void Apply()
        {
            interactive.Alpha = owner.IsEnabled ? 1 : (nfloat)WinoSettingsStyle.DisabledAlpha;
            interactive.UserInteractionEnabled = owner.IsEnabled;
        }
        EventHandler changed = (_, _) => Apply();
        owner.EnabledChanged += changed;
        _subscriptions.Add(new SettingsDisposable(() => owner.EnabledChanged -= changed));
        Apply();
        // Expander items keep their visibility bindings; the card that opened the page is visible by definition.
        if (followsVisibility)
        {
            var observer = owner.AddObserver("hidden", NSKeyValueObservingOptions.Initial | NSKeyValueObservingOptions.New, _ => section.Hidden = owner.Hidden);
            _subscriptions.Add(observer);
        }
    }

    private static UIView CreateScrollRoot(UIView content)
    {
        var root = new UIView { BackgroundColor = UIColor.Clear };
        var zone = new WinoZoneView { Fill = WinoSettingsStyle.PageFill };
        root.AddSubview(zone);
        var guide = root.SafeAreaLayoutGuide;
        var gutter = (nfloat)WinoStyle.ZoneGutter;
        NSLayoutConstraint.ActivateConstraints(
        [
            zone.TopAnchor.ConstraintEqualTo(guide.TopAnchor, gutter),
            zone.LeadingAnchor.ConstraintEqualTo(guide.LeadingAnchor, gutter),
            zone.TrailingAnchor.ConstraintEqualTo(guide.TrailingAnchor, -gutter),
            zone.BottomAnchor.ConstraintEqualTo(root.BottomAnchor)
        ]);

        var scroll = new WinoSettingsScrollView();
        WinoLayout.Fill(scroll, zone.ContentView);
        content.TranslatesAutoresizingMaskIntoConstraints = false;
        scroll.AddSubview(content);
        var frame = scroll.FrameLayoutGuide;
        var pad = (nfloat)SidePadding;
        var preferredWidth = content.WidthAnchor.ConstraintEqualTo(frame.WidthAnchor, 1, -2 * pad);
        preferredWidth.Priority = 990;
        NSLayoutConstraint.ActivateConstraints(
        [
            content.TopAnchor.ConstraintEqualTo(scroll.ContentLayoutGuide.TopAnchor, 16),
            content.BottomAnchor.ConstraintEqualTo(scroll.ContentLayoutGuide.BottomAnchor, -28),
            content.CenterXAnchor.ConstraintEqualTo(frame.CenterXAnchor),
            content.WidthAnchor.ConstraintLessThanOrEqualTo((nfloat)ContentMaxWidth),
            content.LeadingAnchor.ConstraintGreaterThanOrEqualTo(frame.LeadingAnchor, pad),
            preferredWidth
        ]);
        return root;
    }

    /// <summary>The option rows of a choice control, 48pt each, with hairlines inset to the text.</summary>
    private sealed class OptionList : WinoSurfaceView
    {
        private readonly UIView _control;
        private readonly Action _changed;
        private readonly UIStackView _stack;
        private readonly List<OptionRow> _rows = [];
        private IReadOnlyList<string> _options = [];

        public OptionList(UIView control, Action changed)
        {
            _control = control;
            _changed = changed;
            Fill = WinoSettingsStyle.GroupFill;
            Stroke = WinoSettingsStyle.CardStroke;
            CornerRadius = WinoSettingsStyle.CardRadius;
            ClipsToBounds = true;
            TranslatesAutoresizingMaskIntoConstraints = false;
            _stack = WinoLayout.VStack(0);
            _stack.Alignment = UIStackViewAlignment.Fill;
            WinoLayout.Fill(_stack, this);
            Reload();
        }

        /// <summary>Rebuilds the rows when the options changed and moves the checkmark to the bound selection.</summary>
        public void Reload()
        {
            var options = WinoSettingsRowContent.Options(_control);
            if (!options.SequenceEqual(_options))
            {
                _options = options;
                foreach (var view in _stack.ArrangedSubviews) { _stack.RemoveArrangedSubview(view); view.RemoveFromSuperview(); }
                _rows.Clear();
                for (var index = 0; index < options.Count; index++)
                {
                    var captured = index;
                    var row = new OptionRow(options[index], index > 0) { Enabled = WinoSettingsRowContent.IsEnabled(_control, index) };
                    row.TouchUpInside += (_, _) => Choose(captured);
                    _rows.Add(row);
                    _stack.AddArrangedSubview(row);
                }
            }
            Check(WinoSettingsRowContent.SelectedIndex(_control));
        }

        private void Choose(int index)
        {
            Check(index);
            WinoSettingsRowContent.Select(_control, index);
            _changed();
            // The binding may apply the new value on the next run loop turn; read it back once it has.
            DispatchQueue.MainQueue.DispatchAfter(new DispatchTime(DispatchTime.Now, TimeSpan.FromMilliseconds(250)), () =>
            {
                Check(WinoSettingsRowContent.SelectedIndex(_control));
                _changed();
            });
        }

        private void Check(int selected)
        {
            for (var index = 0; index < _rows.Count; index++) _rows[index].IsChecked = index == selected;
        }
    }

    private sealed class OptionRow : UIControl
    {
        private readonly UILabel _title;
        private readonly WinoIconView _check;
        private bool _isChecked;

        public OptionRow(string title, bool separated)
        {
            TranslatesAutoresizingMaskIntoConstraints = false;
            _title = WinoStyle.Label(title, WinoSettingsStyle.CardTitle, WinoStyle.PrimaryText, 2);
            _title.UserInteractionEnabled = false;
            _check = new WinoIconView(WinoIconGlyph.Checkmark, 16, WinoStyle.Accent) { UserInteractionEnabled = false };
            WinoLayout.Size(_check, 16, 16);
            _check.Hidden = true;
            AddSubview(_title);
            AddSubview(_check);
            var pad = (nfloat)WinoSettingsStyle.CardPadding;
            NSLayoutConstraint.ActivateConstraints(
            [
                HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)WinoSettingsStyle.OptionRowHeight),
                _title.LeadingAnchor.ConstraintEqualTo(LeadingAnchor, pad),
                _title.TopAnchor.ConstraintGreaterThanOrEqualTo(TopAnchor, 8),
                _title.BottomAnchor.ConstraintLessThanOrEqualTo(BottomAnchor, -8),
                _title.CenterYAnchor.ConstraintEqualTo(CenterYAnchor),
                _check.LeadingAnchor.ConstraintGreaterThanOrEqualTo(_title.TrailingAnchor, 12),
                _check.TrailingAnchor.ConstraintEqualTo(TrailingAnchor, -pad),
                _check.CenterYAnchor.ConstraintEqualTo(CenterYAnchor)
            ]);
            if (separated)
            {
                var separator = new WinoSeparator { Fill = WinoSettingsStyle.RowSeparator, UserInteractionEnabled = false, TranslatesAutoresizingMaskIntoConstraints = false };
                AddSubview(separator);
                NSLayoutConstraint.ActivateConstraints(
                [
                    separator.TopAnchor.ConstraintEqualTo(TopAnchor),
                    separator.LeadingAnchor.ConstraintEqualTo(LeadingAnchor, pad),
                    separator.TrailingAnchor.ConstraintEqualTo(TrailingAnchor)
                ]);
            }
            IsAccessibilityElement = true;
            AccessibilityLabel = title;
            UpdateTraits();
        }

        public bool IsChecked
        {
            get => _isChecked;
            set { _isChecked = value; _check.Hidden = !value; _check.Tint = WinoStyle.Accent; UpdateTraits(); }
        }

        public override bool Enabled
        {
            get => base.Enabled;
            set { base.Enabled = value; Alpha = value ? 1 : (nfloat)WinoSettingsStyle.DisabledAlpha; UpdateTraits(); }
        }

        public override bool Highlighted
        {
            get => base.Highlighted;
            set { base.Highlighted = value; BackgroundColor = value ? WinoSettingsStyle.SubtleFill : UIColor.Clear; }
        }

        private void UpdateTraits()
        {
            var traits = UIAccessibilityTrait.Button;
            if (_isChecked) traits |= UIAccessibilityTrait.Selected;
            if (!Enabled) traits |= UIAccessibilityTrait.NotEnabled;
            AccessibilityTraits = traits;
        }
    }
}

/// <summary>
/// The scroll view of settings pages: touches reach switches and rows at once (no content-touch delay, so a quick tap on
/// a switch is not lost), and a drag that starts on a control still scrolls the page.
/// </summary>
public sealed class WinoSettingsScrollView : UIScrollView
{
    public WinoSettingsScrollView()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        AlwaysBounceVertical = true;
        DelaysContentTouches = false;
        KeyboardDismissMode = UIScrollViewKeyboardDismissMode.Interactive;
        BackgroundColor = UIColor.Clear;
    }

    public override bool TouchesShouldCancelInContentView(UIView view) => true;
}
