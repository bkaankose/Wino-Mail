using AppKit;
using CoreGraphics;
using Foundation;
using Wino.Core.Domain.Enums;
using Wino.Presentation.AppKit;

namespace Wino.Mail.Controls.AppKit.ToDo;

/// <summary>
/// One task row as a card (Windows ToDoPage TaskRowTemplate, design board "task"): padding
/// 12×10, radius 6, card fill and stroke; round checkbox, title with a metadata line (list with
/// colour dot, Calendar + due date in red when overdue, step summary, My Day in the accent, Note
/// glyph) and the star. Selection swaps the fill and strokes the card in the accent.
/// </summary>
public sealed class WinoTaskCardView : NSTableCellView
{
    public const string ReuseIdentifier = "WinoTaskCard";

    private readonly WinoSurfaceView _card = WinoToDoStyle.Card();
    private readonly WinoRoundCheckbox _check = new();
    private readonly NSTextField _title = WinoStyle.Label(string.Empty, WinoStyle.Body);
    private readonly NSStackView _meta;
    private readonly WinoIconView _listDot;
    private readonly NSTextField _listName;
    private readonly NSStackView _listSegment;
    private readonly (NSStackView Stack, WinoIconView Icon, NSTextField Label) _due;
    private readonly (NSStackView Stack, WinoIconView Icon, NSTextField Label) _steps;
    private readonly (NSStackView Stack, WinoIconView Icon, NSTextField Label) _myDay;
    private readonly WinoIconView _note;
    private readonly NSButton _star;
    private NSTrackingArea? _tracking;
    private bool _selected;
    private bool _hovered;
    private WinoTaskRowModel _model = new();

    public WinoTaskCardView()
    {
        Identifier = ReuseIdentifier;
        WinoLayout.Fill(_card, this, 2, 0, 2, 0);

        _check.Toggled += (_, _) => CompletionToggled?.Invoke(this, EventArgs.Empty);

        _title.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        _title.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);

        _listDot = new WinoIconView(WinoIconGlyph.Dot, 11, WinoStyle.SecondaryText) { Colorful = false };
        _listName = WinoToDoStyle.Caption();
        _listSegment = WinoLayout.HStack(4, _listDot, _listName);
        _due = WinoToDoStyle.MetaSegment(WinoIconGlyph.Calendar);
        _steps = WinoToDoStyle.MetaSegment(WinoIconGlyph.Checkmark);
        _myDay = WinoToDoStyle.MetaSegment(WinoIconGlyph.WeatherSunny, WinoStyle.Accent);
        _note = new WinoIconView(WinoIconGlyph.Note, 11, WinoStyle.SecondaryText) { Colorful = false };
        _meta = WinoLayout.HStack(10, _listSegment, _due.Stack, _steps.Stack, _myDay.Stack, _note);
        _meta.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        _meta.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);

        var text = WinoLayout.VStack(3, _title, _meta);
        text.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        text.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);

        _star = WinoToDoStyle.IconButton(WinoIconGlyph.Star, string.Empty, 15, 24, 24, WinoStyle.SecondaryText);
        _star.Activated += (_, _) => ImportanceToggled?.Invoke(this, EventArgs.Empty);

        var row = WinoLayout.HStack(12, _check, text, WinoLayout.Spacer(), _star);
        row.EdgeInsets = new NSEdgeInsets(10, 12, 10, 12);
        WinoLayout.Fill(row, _card);
        WinoStyle.AccentChanged += AccentChanged;
    }

    /// <summary>Checkbox clicked. The owner runs the toggle command and re-applies the model.</summary>
    public event EventHandler? CompletionToggled;

    /// <summary>Star clicked.</summary>
    public event EventHandler? ImportanceToggled;

    /// <summary>Owner-managed subscription to the bound item; disposed when the cell is reused.</summary>
    public IDisposable? Subscription { get; set; }

    public bool Selected
    {
        get => _selected;
        set { _selected = value; UpdateSurface(); }
    }

    public void Apply(WinoTaskRowModel model)
    {
        _model = model;
        _check.Checked = model.IsCompleted;
        _check.Enabled = model.IsEditable;
        _check.Label = model.CompletionActionText;

        _title.StringValue = model.Title;
        _title.TextColor = model.IsCompleted ? WinoStyle.TertiaryText : WinoStyle.PrimaryText;
        _title.AttributedStringValue = model.IsCompleted
            ? new NSAttributedString(model.Title, new NSStringAttributes
            {
                Font = WinoStyle.Body,
                ForegroundColor = WinoStyle.TertiaryText,
                StrikethroughStyle = (int)NSUnderlineStyle.Single
            })
            : new NSAttributedString(model.Title, new NSStringAttributes { Font = WinoStyle.Body, ForegroundColor = WinoStyle.PrimaryText });

        bool showList = !string.IsNullOrEmpty(model.ListName);
        _listSegment.Hidden = !showList;
        _listName.StringValue = model.ListName ?? string.Empty;
        _listDot.Tint = model.ListColor ?? WinoStyle.SecondaryText;

        _due.Stack.Hidden = string.IsNullOrEmpty(model.DueText);
        _due.Label.StringValue = model.DueText;
        var dueColor = model.IsOverdue ? WinoToDoStyle.Critical : WinoStyle.SecondaryText;
        _due.Label.TextColor = dueColor;
        _due.Icon.Tint = dueColor;

        _steps.Stack.Hidden = string.IsNullOrEmpty(model.StepSummary);
        _steps.Label.StringValue = model.StepSummary;

        _myDay.Stack.Hidden = !model.IsInMyDay;
        _myDay.Label.StringValue = model.MyDayText;
        _note.Hidden = !model.HasNote;

        WinoToDoStyle.SetMonoGlyph(_star, model.IsImportant ? WinoIconGlyph.StarFilled : WinoIconGlyph.Star, 15,
            model.IsImportant ? WinoToDoStyle.Important : WinoStyle.SecondaryText);
        _star.ToolTip = model.ImportanceActionText;
        WinoAccessibility.Label(_star, model.ImportanceActionText);
        _star.Enabled = model.IsEditable;
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
        _card.Fill = _selected ? WinoToDoStyle.SelectedFill : _hovered ? WinoToDoStyle.HoverFill : WinoToDoStyle.CardFill;
        _card.Stroke = _selected ? WinoStyle.Accent : WinoToDoStyle.CardStroke;
    }

    public override void UpdateTrackingAreas()
    {
        base.UpdateTrackingAreas();
        if (_tracking is not null) RemoveTrackingArea(_tracking);
        _tracking = new NSTrackingArea(Bounds, NSTrackingAreaOptions.MouseEnteredAndExited | NSTrackingAreaOptions.ActiveInKeyWindow | NSTrackingAreaOptions.InVisibleRect, this, null);
        AddTrackingArea(_tracking);
    }

    public override void MouseEntered(NSEvent theEvent) { _hovered = true; UpdateSurface(); }
    public override void MouseExited(NSEvent theEvent) { _hovered = false; UpdateSurface(); }

    public override void PrepareForReuse()
    {
        base.PrepareForReuse();
        Subscription?.Dispose();
        Subscription = null;
        _hovered = false;
        _selected = false;
        UpdateSurface();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            WinoStyle.AccentChanged -= AccentChanged;
            Subscription?.Dispose();
            Subscription = null;
        }
        base.Dispose(disposing);
    }
}

/// <summary>Row container for <see cref="WinoTaskCardView"/>: the card draws its own selection, so this draws nothing.</summary>
public sealed class WinoTaskTableRowView : NSTableRowView
{
    public const string ReuseIdentifier = "WinoTaskTableRow";

    public WinoTaskTableRowView() => Identifier = ReuseIdentifier;

    public override NSBackgroundStyle InteriorBackgroundStyle => NSBackgroundStyle.Normal;

    public override bool Selected
    {
        get => base.Selected;
        set
        {
            base.Selected = value;
            if (NumberOfColumns > 0 && ViewAtColumn(0) is WinoTaskCardView card) card.Selected = value;
        }
    }

    public override void DrawSelection(CGRect dirtyRect) { }
    public override void DrawBackground(CGRect dirtyRect) { }
    public override void DrawSeparator(CGRect dirtyRect) { }
}

/// <summary>Group header for smart views (Windows TaskGroup header): account name in BodyStrong secondary, count in tertiary.</summary>
public sealed class WinoTaskGroupHeaderView : NSTableCellView
{
    public const string ReuseIdentifier = "WinoTaskGroupHeader";

    private readonly NSTextField _key = WinoStyle.Label(string.Empty, WinoStyle.BodyStrong, WinoStyle.SecondaryText);
    private readonly NSTextField _count = WinoStyle.Label(string.Empty, WinoStyle.Body, WinoStyle.TertiaryText);

    public WinoTaskGroupHeaderView()
    {
        Identifier = ReuseIdentifier;
        var stack = WinoLayout.HStack(8, _key, _count);
        stack.EdgeInsets = new NSEdgeInsets(12, 8, 4, 8);
        WinoLayout.Fill(stack, this);
    }

    public void Apply(string key, string count)
    {
        _key.StringValue = key;
        _count.StringValue = count;
    }
}
