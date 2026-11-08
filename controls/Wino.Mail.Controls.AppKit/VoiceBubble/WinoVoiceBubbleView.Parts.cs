using AppKit;
using CoreGraphics;
using Foundation;
using ObjCRuntime;
using Wino.Mail.Controls.AppKit.Common;
using Wino.Mail.Controls.Core.AccountIcon;
using Wino.Mail.Controls.Core.VoiceBubble;
using Wino.Presentation.AppKit;

namespace Wino.Mail.Controls.AppKit.VoiceBubble;

public sealed partial class WinoVoiceBubbleView
{
    private const double ChoiceListHeight = 216;
    private const double GroupIconSize = 24;
    private const double GroupIconOverlap = 0.66;

    private NSStackView _stepsPanel = null!;
    private NSStackView _stepsList = null!;
    private NSButton _stepsConfirm = null!;
    private NSButton _stepsDecline = null!;
    private IReadOnlyList<(VoiceBubbleStepGroup Group, NSButton Toggle)> _groupToggles = [];

    private NSStackView _questionPanel = null!;
    private NSTextField _questionHeading = null!;
    private NSSearchField _questionSearch = null!;
    private ChoiceTableView _questionTable = null!;
    private NSTextField _questionEmpty = null!;
    private NSTextField _questionCaption = null!;
    private NSButton _questionCancel = null!;
    private List<VoiceBubbleChoice> _choices = [];
    private string _questionFilter = string.Empty;
    private ChoiceTableSource? _choiceSource;
    private SearchFieldDelegate? _searchDelegate;

    private enum GlyphKind
    {
        Success,
        Error,
        Warning,
        Question
    }

    /// <summary>Replaces the question's choices in one batch and selects the first one that can be picked.</summary>
    public void SetChoices(List<VoiceBubbleChoice> choices)
    {
        _choices = choices;
        _questionTable.ReloadData();
        _questionEmpty.Hidden = choices.Count > 0;

        var first = choices.FindIndex(static choice => choice.IsItem);
        if (first >= 0)
        {
            _questionTable.SelectRow(first, false);
            _questionTable.ScrollRowToVisible(first);
        }
        else
        {
            _questionTable.DeselectAll(null);
        }
    }

    /// <summary>Empties the filter box for a new question without raising <see cref="QuestionFilterChanged"/>.</summary>
    public void ClearQuestionFilter()
    {
        _questionFilter = string.Empty;
        _questionSearch.StringValue = string.Empty;
    }

    private void BuildStepsPanel()
    {
        _stepsList = WinoLayout.VStack(10);
        _stepsList.Alignment = NSLayoutAttribute.Leading;
        _stepsConfirm = CreateButton(isDefault: true, () => ConfirmRequested?.Invoke(this, EventArgs.Empty));
        _stepsDecline = CreateButton(isDefault: false, () => CancelRequested?.Invoke(this, EventArgs.Empty));
        var buttons = WinoLayout.HStack(6, WinoLayout.Spacer(), _stepsDecline, _stepsConfirm);
        _stepsPanel = WinoLayout.VStack(10, _stepsList, buttons);
        _stepsPanel.Alignment = NSLayoutAttribute.Leading;
        _stepsPanel.EdgeInsets = new NSEdgeInsets(6, 0, 0, 0);
        WinoLayout.Size(_stepsPanel, PanelWidth, -1);
        buttons.WidthAnchor.ConstraintEqualTo(_stepsPanel.WidthAnchor).Active = true;
    }

    /// <summary>
    /// The plan as cards per account. With more than one account that has steps, each account gets a checkbox and
    /// the button confirms only the checked ones.
    /// </summary>
    private void BuildSteps()
    {
        foreach (var view in _stepsList.ArrangedSubviews)
        {
            _stepsList.RemoveArrangedSubview(view);
            view.RemoveFromSuperview();
        }

        var toggles = new List<(VoiceBubbleStepGroup, NSButton)>();
        _stepsConfirm.Title = ConfirmText;
        _stepsDecline.Title = DeclineText;
        _stepsConfirm.Enabled = true;

        if (ConfirmationGroups is { Count: > 0 } groups)
        {
            var perAccount = groups.Count(static group => group.CanSelect) > 1;
            if (perAccount)
                _stepsConfirm.Title = ConfirmSelectedText;

            foreach (var group in groups)
            {
                var section = WinoLayout.VStack(4);
                section.Alignment = NSLayoutAttribute.Leading;

                if (group.Kind != VoiceBubbleStepGroupKind.None)
                {
                    NSButton? toggle = null;
                    if (perAccount && group.CanSelect)
                    {
                        toggle = NSButton.CreateCheckbox(string.Empty, () => { });
                        toggle.State = group.IsSelected ? NSCellStateValue.On : NSCellStateValue.Off;
                        toggle.Activated += (_, _) =>
                        {
                            group.IsSelected = toggle.State == NSCellStateValue.On;
                            UpdateSelectedConfirm();
                        };
                        WinoAccessibility.Label(toggle, group.Title);
                        toggles.Add((group, toggle));
                    }

                    section.AddArrangedSubview(CreateGroupHeader(group, toggle));
                }

                AddStepRows(section, group.Steps, numbered: group.Steps.Count > 1, useStepNumbers: false);
                foreach (var notice in group.Notices)
                    section.AddArrangedSubview(CreateNotice(notice));

                _stepsList.AddArrangedSubview(section);
                section.WidthAnchor.ConstraintEqualTo((nfloat)PanelWidth).Active = true;
            }
        }
        else if (ConfirmationSteps is { Count: > 0 } steps)
        {
            var section = WinoLayout.VStack(4);
            section.Alignment = NSLayoutAttribute.Leading;
            AddStepRows(section, steps, numbered: true, useStepNumbers: true);
            _stepsList.AddArrangedSubview(section);
        }

        _groupToggles = toggles;
        UpdateSelectedConfirm();
    }

    private void UpdateSelectedConfirm()
    {
        if (_groupToggles.Count > 0)
            _stepsConfirm.Enabled = _groupToggles.Any(static entry => entry.Group.IsSelected);
    }

    private NSView CreateGroupHeader(VoiceBubbleStepGroup group, NSButton? toggle)
    {
        NSView icon;
        if (group.Kind == VoiceBubbleStepGroupKind.Unresolved)
        {
            icon = CreateGlyph(GlyphKind.Warning);
            WinoLayout.Size(icon, GroupIconSize, GroupIconSize);
        }
        else
        {
            icon = CreateAccountStack(group.Accounts.Take(group.Kind == VoiceBubbleStepGroupKind.Accounts ? 3 : 1).ToArray());
        }

        var texts = WinoLayout.VStack(0);
        texts.Alignment = NSLayoutAttribute.Leading;
        if (!string.IsNullOrWhiteSpace(group.Title))
            texts.AddArrangedSubview(WinoStyle.Label(group.Title, WinoStyle.BodyStrong));
        if (!string.IsNullOrWhiteSpace(group.Subtitle))
            texts.AddArrangedSubview(WinoStyle.Label(group.Subtitle, NSFont.SystemFontOfSize(12), WinoStyle.SecondaryText));
        texts.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);

        var row = toggle is null
            ? WinoLayout.HStack(8, icon, texts)
            : WinoLayout.HStack(8, icon, texts, WinoLayout.Spacer(), toggle);
        row.Alignment = NSLayoutAttribute.CenterY;
        row.WidthAnchor.ConstraintEqualTo((nfloat)PanelWidth).Active = true;
        row.AccessibilityElement = true;
        WinoAccessibility.Label(row, string.Join(", ", new[] { group.Title, group.Subtitle }.Where(static text => !string.IsNullOrWhiteSpace(text))));
        return row;
    }

    /// <summary>Up to three overlapping account icons, the first on top.</summary>
    private static NSView CreateAccountStack(IReadOnlyList<IAccountIconInfo> accounts)
    {
        var count = Math.Max(1, accounts.Count);
        var width = GroupIconSize * (1 + ((count - 1) * GroupIconOverlap));
        var container = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Size(container, width, GroupIconSize);

        for (var index = accounts.Count - 1; index >= 0; index--)
        {
            var icon = new WinoAccountIconView(GroupIconSize)
            {
                Account = accounts[index],
                TranslatesAutoresizingMaskIntoConstraints = true,
                Frame = new CGRect(index * GroupIconSize * GroupIconOverlap, 0, GroupIconSize, GroupIconSize)
            };
            container.AddSubview(icon);
        }

        return container;
    }

    private void AddStepRows(NSStackView section, IReadOnlyList<VoiceBubbleStep> steps, bool numbered, bool useStepNumbers)
    {
        for (var index = 0; index < steps.Count; index++)
        {
            var step = steps[index];
            var bodyWidth = numbered ? PanelWidth - 20 : PanelWidth;
            var body = WinoLayout.VStack(2);
            body.Alignment = NSLayoutAttribute.Leading;
            var text = CreateWrappingLabel(WinoStyle.Body, bodyWidth);
            text.AttributedStringValue = ToAttributed(step.Text, WinoStyle.Body, WinoStyle.BodyStrong);
            body.AddArrangedSubview(text);
            if (!string.IsNullOrWhiteSpace(step.Detail))
            {
                var detail = CreateWrappingLabel(NSFont.SystemFontOfSize(12), bodyWidth);
                detail.StringValue = step.Detail;
                detail.TextColor = WinoStyle.SecondaryText;
                body.AddArrangedSubview(detail);
            }

            if (!numbered)
            {
                section.AddArrangedSubview(body);
                continue;
            }

            var number = WinoStyle.Label((useStepNumbers ? step.Number ?? index + 1 : index + 1).ToString(System.Globalization.CultureInfo.CurrentCulture),
                NSFont.SystemFontOfSize(12, NSFontWeight.Semibold), NSColor.SecondaryLabel);
            number.Alignment = NSTextAlignment.Center;
            WinoLayout.Size(number, 14, -1);
            var row = WinoLayout.HStack(6, number, body);
            row.Alignment = NSLayoutAttribute.FirstBaseline;
            section.AddArrangedSubview(row);
        }
    }

    private NSView CreateNotice(string message)
    {
        var text = CreateWrappingLabel(NSFont.SystemFontOfSize(12), PanelWidth - 16 - 24);
        var attributed = new NSMutableAttributedString(new NSAttributedString(NoticeTitle, new NSStringAttributes { Font = NSFont.SystemFontOfSize(12, NSFontWeight.Semibold), ForegroundColor = NSColor.Label }));
        attributed.Append(new NSAttributedString(" " + message, new NSStringAttributes { Font = NSFont.SystemFontOfSize(12), ForegroundColor = NSColor.Label }));
        text.AttributedStringValue = attributed;

        var row = WinoLayout.HStack(8, CreateGlyph(GlyphKind.Warning), text);
        row.Alignment = NSLayoutAttribute.Top;
        row.EdgeInsets = new NSEdgeInsets(6, 8, 6, 8);
        row.WantsLayer = true;
        row.Layer!.CornerRadius = 4;
        row.Layer.BackgroundColor = NSColor.SystemOrange.ColorWithAlphaComponent(0.14f).CGColor;
        row.WidthAnchor.ConstraintEqualTo((nfloat)PanelWidth).Active = true;
        row.AccessibilityElement = true;
        WinoAccessibility.Label(row, $"{NoticeTitle}. {message}");
        return row;
    }

    private void BuildQuestionPanel()
    {
        _questionHeading = CreateWrappingLabel(WinoStyle.BodyStrong, PanelWidth - 24);
        var heading = WinoLayout.HStack(8, CreateGlyph(GlyphKind.Question), _questionHeading);
        heading.Alignment = NSLayoutAttribute.Top;

        _searchDelegate = new SearchFieldDelegate(this);
        _questionSearch = new NSSearchField { TranslatesAutoresizingMaskIntoConstraints = false, Delegate = _searchDelegate };
        _questionSearch.Cell.SendsWholeSearchString = false;

        _choiceSource = new ChoiceTableSource(this);
        _questionTable = new ChoiceTableView(this)
        {
            HeaderView = null,
            Style = NSTableViewStyle.Inset,
            BackgroundColor = NSColor.Clear,
            IntercellSpacing = new CGSize(0, 0),
            AllowsEmptySelection = true,
            AllowsMultipleSelection = false,
            UsesAutomaticRowHeights = false,
            Source = _choiceSource
        };
        _questionTable.AddColumn(new NSTableColumn("choice") { ResizingMask = NSTableColumnResizing.Autoresizing });
        _questionTable.Action = new Selector("choiceClicked:");
        _questionTable.Target = _choiceSource;

        var scroll = new NSScrollView
        {
            DocumentView = _questionTable,
            HasVerticalScroller = true,
            AutohidesScrollers = true,
            DrawsBackground = false,
            BorderType = NSBorderType.NoBorder,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        WinoLayout.Size(scroll, PanelWidth, ChoiceListHeight);

        _questionEmpty = WinoStyle.Label(string.Empty, NSFont.SystemFontOfSize(12), WinoStyle.SecondaryText, maximumLines: 0);
        _questionEmpty.Alignment = NSTextAlignment.Center;
        scroll.AddSubview(_questionEmpty);
        NSLayoutConstraint.ActivateConstraints(
        [
            _questionEmpty.CenterXAnchor.ConstraintEqualTo(scroll.CenterXAnchor),
            _questionEmpty.CenterYAnchor.ConstraintEqualTo(scroll.CenterYAnchor),
            _questionEmpty.WidthAnchor.ConstraintLessThanOrEqualTo(scroll.WidthAnchor, 1, -24)
        ]);

        _questionCaption = CreateWrappingLabel(NSFont.SystemFontOfSize(12), PanelWidth - 90);
        _questionCaption.TextColor = WinoStyle.SecondaryText;
        _questionCancel = CreateButton(isDefault: false, () => CancelRequested?.Invoke(this, EventArgs.Empty));
        var footer = WinoLayout.HStack(8, _questionCaption, WinoLayout.Spacer(), _questionCancel);
        footer.Alignment = NSLayoutAttribute.CenterY;

        _questionPanel = WinoLayout.VStack(8, heading, _questionSearch, scroll, footer);
        _questionPanel.Alignment = NSLayoutAttribute.Leading;
        _questionPanel.EdgeInsets = new NSEdgeInsets(6, 0, 0, 0);
        WinoLayout.Size(_questionPanel, PanelWidth, -1);
        _questionSearch.WidthAnchor.ConstraintEqualTo(_questionPanel.WidthAnchor).Active = true;
        footer.WidthAnchor.ConstraintEqualTo(_questionPanel.WidthAnchor).Active = true;
    }

    private void ApplyQuestion()
    {
        _questionHeading.StringValue = QuestionText ?? string.Empty;
        _questionSearch.Hidden = !IsQuestionSearchable;
        _questionSearch.PlaceholderString = QuestionSearchPlaceholder;
        WinoAccessibility.Label(_questionSearch, QuestionSearchPlaceholder);
        WinoAccessibility.Label(_questionTable, QuestionText);
        _questionEmpty.StringValue = QuestionEmptyText;
        _questionEmpty.Hidden = _choices.Count > 0;
        _questionCaption.StringValue = QuestionCaption ?? string.Empty;
        _questionCaption.Hidden = string.IsNullOrWhiteSpace(QuestionCaption);
        _questionCancel.Title = QuestionCancelText;
    }

    private void OnQuestionFilterChanged(string filter)
    {
        if (string.Equals(filter, _questionFilter, StringComparison.Ordinal))
            return;

        _questionFilter = filter;
        QuestionFilterChanged?.Invoke(this, filter);
    }

    /// <summary>Moves the selection one item up or down, skipping account headers; it stops at either end.</summary>
    private void MoveChoice(int direction)
    {
        var index = (int)_questionTable.SelectedRow;
        for (var next = index + direction; next >= 0 && next < _choices.Count; next += direction)
        {
            if (!_choices[next].IsItem)
                continue;

            _questionTable.SelectRow(next, false);
            _questionTable.ScrollRowToVisible(next);
            return;
        }
    }

    private void SelectChoice(nint row)
    {
        if (row >= 0 && row < _choices.Count && _choices[(int)row].IsItem)
            ChoiceSelected?.Invoke(this, _choices[(int)row]);
    }

    private static NSTextField CreateWrappingLabel(NSFont font, double maxWidth)
    {
        var label = WinoStyle.Label(string.Empty, font, maximumLines: 0);
        label.PreferredMaxLayoutWidth = (nfloat)maxWidth;
        label.WidthAnchor.ConstraintLessThanOrEqualTo((nfloat)maxWidth).Active = true;
        return label;
    }

    private static NSButton CreateButton(bool isDefault, Action action)
    {
        var button = NSButton.CreateButton(string.Empty, action);
        button.BezelStyle = NSBezelStyle.Push;
        button.ControlSize = NSControlSize.Regular;
        button.TranslatesAutoresizingMaskIntoConstraints = false;
        if (isDefault)
            button.KeyEquivalent = "\r";
        return button;
    }

    private static NSImageView CreateGlyph(GlyphKind kind)
    {
        var view = new NSImageView { TranslatesAutoresizingMaskIntoConstraints = false };
        ApplyGlyph(view, kind);
        WinoLayout.Size(view, 16, 16);
        ((NSView)view).AccessibilityElement = false;
        return view;
    }

    private static void ApplyGlyph(NSImageView view, GlyphKind kind)
    {
        var (symbol, color) = kind switch
        {
            GlyphKind.Success => ("checkmark.circle.fill", WinoStyle.Success),
            GlyphKind.Error => ("xmark.octagon.fill", WinoStyle.Critical),
            GlyphKind.Warning => ("exclamationmark.triangle.fill", WinoStyle.Caution),
            _ => ("questionmark.circle.fill", WinoStyle.Informational)
        };
        view.Image = WinoStyle.Symbol(symbol, null, 14);
        view.ContentTintColor = color;
    }

    private static NSAttributedString ToAttributed(VoiceBubbleText? text, NSFont font, NSFont strongFont)
    {
        var result = new NSMutableAttributedString();
        if (text is null)
            return result;

        foreach (var span in text.Spans)
            result.Append(new NSAttributedString(span.Text, new NSStringAttributes { Font = span.IsStrong ? strongFont : font, ForegroundColor = NSColor.Label }));

        return result;
    }

    private sealed class SearchFieldDelegate(WinoVoiceBubbleView owner) : NSSearchFieldDelegate
    {
        public override void Changed(NSNotification notification) => owner.OnQuestionFilterChanged(owner._questionSearch.StringValue ?? string.Empty);

        public override bool DoCommandBySelector(NSControl control, NSTextView textView, Selector commandSelector)
        {
            switch (commandSelector.Name)
            {
                case "moveDown:":
                    owner.MoveChoice(1);
                    return true;
                case "moveUp:":
                    owner.MoveChoice(-1);
                    return true;
                case "insertNewline:":
                    owner.SelectChoice(owner._questionTable.SelectedRow);
                    return true;
                case "cancelOperation:":
                    owner.CancelRequested?.Invoke(owner, EventArgs.Empty);
                    return true;
                default:
                    return false;
            }
        }
    }

    /// <summary>Return and Space pick the selected choice; Escape cancels the question.</summary>
    private sealed class ChoiceTableView(WinoVoiceBubbleView owner) : NSTableView
    {
        public override void KeyDown(NSEvent theEvent)
        {
            switch (theEvent.KeyCode)
            {
                case 36 or 76 or 49:
                    owner.SelectChoice(SelectedRow);
                    return;
                case 53:
                    owner.CancelRequested?.Invoke(owner, EventArgs.Empty);
                    return;
            }

            base.KeyDown(theEvent);
        }
    }

    private sealed class ChoiceTableSource(WinoVoiceBubbleView owner) : NSTableViewSource
    {
        public override nint GetRowCount(NSTableView tableView) => owner._choices.Count;

        public override bool ShouldSelectRow(NSTableView tableView, nint row) => row < owner._choices.Count && owner._choices[(int)row].IsItem;

        public override nfloat GetRowHeight(NSTableView tableView, nint row)
        {
            if (row >= owner._choices.Count)
                return 28;

            var choice = owner._choices[(int)row];
            return choice.IsHeader ? 30 : choice.HasDetail ? 40 : 28;
        }

        public override NSView GetViewForItem(NSTableView tableView, NSTableColumn tableColumn, nint row)
        {
            var choice = owner._choices[(int)row];
            NSStackView content;
            if (choice.IsHeader)
            {
                content = WinoLayout.HStack(6);
                if (choice.Account is { } account)
                {
                    var icon = new WinoAccountIconView(16) { Account = account };
                    WinoLayout.Size(icon, 16, 16);
                    content.AddArrangedSubview(icon);
                }

                content.AddArrangedSubview(WinoStyle.Label(choice.Title, NSFont.SystemFontOfSize(12, NSFontWeight.Semibold), WinoStyle.SecondaryText));
                content.EdgeInsets = new NSEdgeInsets(8, 0, 2, 0);
                content.Alignment = NSLayoutAttribute.Bottom;
            }
            else
            {
                content = WinoLayout.HStack(8);
                if (choice.HasItemAccount && choice.Account is { } account)
                {
                    var icon = new WinoAccountIconView(20) { Account = account };
                    WinoLayout.Size(icon, 20, 20);
                    content.AddArrangedSubview(icon);
                }

                var texts = WinoLayout.VStack(0, WinoStyle.Label(choice.Title, WinoStyle.Body));
                texts.Alignment = NSLayoutAttribute.Leading;
                if (choice.HasDetail)
                    texts.AddArrangedSubview(WinoStyle.Label(choice.Detail, NSFont.SystemFontOfSize(12), WinoStyle.SecondaryText));
                texts.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
                content.AddArrangedSubview(texts);
                content.Alignment = NSLayoutAttribute.CenterY;
            }

            var cell = new NSTableCellView();
            content.TranslatesAutoresizingMaskIntoConstraints = false;
            cell.AddSubview(content);
            WinoLayout.Fill(content, cell, 0);
            WinoAccessibility.Label(cell, choice.Title);
            return cell;
        }

        [Export("choiceClicked:")]
        public void ChoiceClicked(NSObject sender) => owner.SelectChoice(owner._questionTable.ClickedRow);
    }
}
