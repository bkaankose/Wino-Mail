using Foundation;
using UIKit;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.Core.ToDo;
using Wino.Presentation.UIKit;

namespace Wino.Mail.Controls.UIKit.ToDo;

/// <summary>
/// One task row (Windows ToDoPage TaskRowTemplate; UIKit twin of the AppKit <c>WinoTaskCardView</c>) over the shared
/// <see cref="WinoTaskRowModel"/>: round checkbox, title (16) with a metadata line (list with colour dot, due date in
/// red when overdue, step summary, My Day in the accent, Note glyph) and the star. In the iOS canvas look the row is
/// flat on the zone: no card, a rounded fill only while pressed or selected, an optional hairline above it
/// (<see cref="ShowsSeparator"/>), and completed rows at 60% opacity. Swipe actions belong to the page.
/// </summary>
public sealed class WinoTaskRowCell : UITableViewCell
{
    public new const string ReuseIdentifier = "WinoTaskRow";

    private readonly WinoSurfaceView _card = new() { CornerRadius = WinoToDoStyle.CardRadius };
    private readonly UIView _separator = new() { TranslatesAutoresizingMaskIntoConstraints = false, BackgroundColor = WinoToDoStyle.RowSeparator, Hidden = true };
    private readonly UIStackView _content;
    private readonly WinoRoundCheckbox _check = new();
    private readonly UILabel _title = WinoStyle.Label(string.Empty, WinoToDoStyle.TaskTitle);
    private readonly UIStackView _meta;
    private readonly WinoIconView _listDot;
    private readonly UILabel _listName;
    private readonly UIStackView _listSegment;
    private readonly (UIStackView Stack, WinoIconView Icon, UILabel Label) _due;
    private readonly (UIStackView Stack, WinoIconView Icon, UILabel Label) _steps;
    private readonly (UIStackView Stack, WinoIconView Icon, UILabel Label) _myDay;
    private readonly WinoIconView _note;
    private readonly UIButton _star;
    private bool _hovered;
    private WinoTaskRowModel _model = new();
    private IDisposable? _subscription;

    public WinoTaskRowCell() : this(UITableViewCellStyle.Default, new NSString(ReuseIdentifier)) { }

    [Export("initWithStyle:reuseIdentifier:")]
    public WinoTaskRowCell(UITableViewCellStyle style, NSString reuseIdentifier) : base(style, reuseIdentifier)
    {
        SelectionStyle = UITableViewCellSelectionStyle.None;
        BackgroundColor = UIColor.Clear;
        WinoLayout.Fill(_card, ContentView, 1, 0, 1, 0);
        ContentView.AddSubview(_separator);
        NSLayoutConstraint.ActivateConstraints(
        [
            _separator.TopAnchor.ConstraintEqualTo(ContentView.TopAnchor),
            _separator.LeadingAnchor.ConstraintEqualTo(ContentView.LeadingAnchor),
            _separator.TrailingAnchor.ConstraintEqualTo(ContentView.TrailingAnchor),
            _separator.HeightAnchor.ConstraintEqualTo(1 / UIScreen.MainScreen.Scale)
        ]);

        _check.Toggled += (_, _) => CompletionToggled?.Invoke(this, EventArgs.Empty);
        WinoLayout.Priorities(_title, UILayoutConstraintAxis.Horizontal, 1, 250);

        _listDot = new WinoIconView(WinoIconGlyph.Dot, 11, WinoStyle.SecondaryText) { Colorful = false };
        _listName = WinoToDoStyle.Caption();
        _listSegment = WinoLayout.HStack(4, _listDot, _listName);
        _due = WinoToDoStyle.MetaSegment(WinoIconGlyph.Calendar);
        _steps = WinoToDoStyle.MetaSegment(WinoIconGlyph.Checkmark);
        _myDay = WinoToDoStyle.MetaSegment(WinoIconGlyph.WeatherSunny, WinoStyle.Accent);
        _note = new WinoIconView(WinoIconGlyph.Note, 11, WinoStyle.SecondaryText) { Colorful = false };
        _meta = WinoLayout.HStack(10, _listSegment, _due.Stack, _steps.Stack, _myDay.Stack, _note);
        WinoLayout.Priorities(_meta, UILayoutConstraintAxis.Horizontal, 1, 250);

        var text = WinoLayout.VStack(3, _title, _meta);
        WinoLayout.Priorities(text, UILayoutConstraintAxis.Horizontal, 1, 250);

        _star = WinoToDoStyle.IconButton(WinoIconGlyph.Star, string.Empty, 16, 44, 44, WinoStyle.SecondaryText);
        _star.PrimaryActionTriggered += (_, _) => ImportanceToggled?.Invoke(this, EventArgs.Empty);

        _content = WinoLayout.HStack(12, _check, text, WinoLayout.Spacer(), _star);
        var row = WinoLayout.Insets(_content, 6, 8, 6, 0);
        WinoLayout.Fill(row, _card);
        AddGestureRecognizer(new UIHoverGestureRecognizer(recognizer =>
        {
            _hovered = recognizer.State is UIGestureRecognizerState.Began or UIGestureRecognizerState.Changed;
            UpdateSurface();
        }));
        WinoStyle.AccentChanged += AccentChanged;
        UpdateSurface();
    }

    /// <summary>Checkbox tapped. The owner runs the toggle command and re-applies the model.</summary>
    public event EventHandler? CompletionToggled;

    /// <summary>Star tapped.</summary>
    public event EventHandler? ImportanceToggled;

    /// <summary>Owner-managed subscription to the bound item; disposed when the cell is reused.</summary>
    public IDisposable? Subscription
    {
        get => _subscription;
        set
        {
            if (ReferenceEquals(_subscription, value)) return;
            _subscription?.Dispose();
            _subscription = value;
        }
    }

    public WinoTaskRowModel Model => _model;

    /// <summary>A hairline above the row (the iPhone list separates its rows; the iPad list does not).</summary>
    public bool ShowsSeparator
    {
        get => !_separator.Hidden;
        set => _separator.Hidden = !value;
    }

    public void Apply(WinoTaskRowModel model)
    {
        _model = model;
        _check.Checked = model.IsCompleted;
        _check.Enabled = model.IsEditable;
        _check.Label = model.CompletionActionText;

        var attributes = new UIStringAttributes
        {
            Font = WinoToDoStyle.TaskTitle,
            ForegroundColor = WinoStyle.PrimaryText
        };
        if (model.IsCompleted) attributes.StrikethroughStyle = NSUnderlineStyle.Single;
        _title.AttributedText = new NSAttributedString(model.Title, attributes);
        _content.Alpha = model.IsCompleted ? (nfloat)WinoToDoStyle.CompletedOpacity : 1;

        bool showList = !string.IsNullOrEmpty(model.ListName);
        _listSegment.Hidden = !showList;
        _listName.Text = model.ListName ?? string.Empty;
        _listDot.Tint = WinoStyle.FromHexString(model.ListColorHex) ?? WinoStyle.SecondaryText;

        _due.Stack.Hidden = string.IsNullOrEmpty(model.DueText);
        _due.Label.Text = model.DueText;
        var dueColor = model.IsOverdue ? WinoToDoStyle.Critical : WinoStyle.SecondaryText;
        _due.Label.TextColor = dueColor;
        _due.Icon.Tint = dueColor;

        _steps.Stack.Hidden = string.IsNullOrEmpty(model.StepSummary);
        _steps.Label.Text = model.StepSummary;

        _myDay.Stack.Hidden = !model.IsInMyDay;
        _myDay.Label.Text = model.MyDayText;
        _note.Hidden = !model.HasNote;
        _meta.Hidden = !showList && _due.Stack.Hidden && _steps.Stack.Hidden && _myDay.Stack.Hidden && _note.Hidden;

        WinoAccessibility.Label(_star, model.ImportanceActionText);
        WinoToDoStyle.SetMonoGlyph(_star, model.IsImportant ? WinoIconGlyph.StarFilled : WinoIconGlyph.Star, 16,
            model.IsImportant ? WinoToDoStyle.Important : WinoStyle.SecondaryText);
        _star.Enabled = model.IsEditable;
        _title.AccessibilityLabel = string.Join(", ", new[] { model.Title, model.ListName, model.DueText, model.StepSummary }.Where(part => !string.IsNullOrEmpty(part)));
        UpdateSurface();
    }

    public override void SetSelected(bool selected, bool animated)
    {
        base.SetSelected(selected, animated);
        UpdateSurface();
    }

    public override void SetHighlighted(bool highlighted, bool animated)
    {
        base.SetHighlighted(highlighted, animated);
        UpdateSurface();
    }

    private void AccentChanged(object? sender, EventArgs e)
    {
        _myDay.Icon.Tint = WinoStyle.Accent;
        _myDay.Label.TextColor = WinoStyle.Accent;
        UpdateSurface();
    }

    private void UpdateSurface()
    {
        bool selected = Selected;
        _card.Fill = selected ? WinoToDoStyle.SelectedFill : _hovered || Highlighted ? WinoToDoStyle.HoverFill : UIColor.Clear;
        _card.Stroke = null;
    }

    public override void PrepareForReuse()
    {
        base.PrepareForReuse();
        Subscription = null;
        _hovered = false;
        UpdateSurface();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            WinoStyle.AccentChanged -= AccentChanged;
            Subscription = null;
            CompletionToggled = null;
            ImportanceToggled = null;
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// Group header for smart views and the completed group (Windows TaskGroup header): the key in 13pt semibold secondary
/// text and the count beside it, as the iOS canvas draws "Completed · 3".
/// </summary>
public sealed class WinoTaskGroupHeaderView : UITableViewHeaderFooterView
{
    public new const string ReuseIdentifier = "WinoTaskGroupHeader";

    private readonly UILabel _key = WinoStyle.Label(string.Empty, WinoToDoStyle.GroupHeader, WinoStyle.SecondaryText);
    private readonly UILabel _count = WinoStyle.Label(string.Empty, WinoToDoStyle.GroupHeader, WinoStyle.TertiaryText);

    public WinoTaskGroupHeaderView() : this(new NSString(ReuseIdentifier)) { }

    [Export("initWithReuseIdentifier:")]
    public WinoTaskGroupHeaderView(NSString reuseIdentifier) : base(reuseIdentifier)
    {
        var stack = WinoLayout.Insets(WinoLayout.HStack(6, _key, _count), 12, 8, 4, 8);
        WinoLayout.Fill(stack, ContentView);
        _key.AccessibilityTraits = UIAccessibilityTrait.Header;
    }

    public void Apply(string key, string count)
    {
        _key.Text = key;
        _count.Text = count;
    }
}
