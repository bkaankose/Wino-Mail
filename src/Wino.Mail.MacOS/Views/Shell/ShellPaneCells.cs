using System.Collections.Specialized;
using System.ComponentModel;
using AppKit;
using CoreGraphics;
using Wino.Calendar.ViewModels.Data;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.MenuItems;
using Wino.Core.Domain.Models.Calendar;
using Wino.Mail.Controls.AppKit.Common;
using Wino.Mail.Controls.AppKit.Shell;
using Wino.Mail.Controls.Core.AccountIcon;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Shell;

/// <summary>Services the pane cells need from the shell.</summary>
internal sealed record ShellPaneContext(Func<MailAccount, IAccountIconInfo?> AccountIcon, Func<DayOfWeek> FirstDayOfWeek);

/// <summary>
/// The general pane row, configured per menu item type to match the Windows templates: New item,
/// account (icon, name, address, sync bar, attention, unread pill, chevron), folder (special-folder
/// glyph, custom colours, pill), task views and lists, contact filters and calendars.
/// </summary>
internal sealed class ShellPaneCell : NSTableCellView
{
    public const string ReuseIdentifier = "WinoShellPaneCell";
    private const double AccountIconSize = 28;
    // Leading insets from the cell edge (the pane edge on root rows). The selection pipe ends at
    // x = 7, so avatars sit 5pt and 16pt glyphs 9pt beside it, as in the Windows NavigationView.
    private const int AvatarInset = 12;
    private const int GlyphInset = 14;
    private const int IconTextSpacing = 10;

    private readonly ShellPaneContext _context;
    private readonly NSView _leadingBox = new() { TranslatesAutoresizingMaskIntoConstraints = false };
    private readonly NSLayoutConstraint _leadingWidth;
    private readonly WinoIconView _glyph = new(WinoIconGlyph.None, 16);
    private readonly WinoAccountIconView _accountIcon = new(AccountIconSize);
    private readonly WinoSurfaceView _dot = new();
    private readonly NSLayoutConstraint _dotSize;
    private readonly NSButton _check = WinoCheckbox.Create(null);
    private readonly WinoSurfaceView _titleChip = new() { CornerRadius = 3 };
    private readonly NSTextField _title;
    private readonly NSLayoutConstraint[] _chipInsets;
    private readonly NSTextField _subtitle;
    private readonly WinoSyncBar _sync = new();
    private readonly NSButton _attention;
    private readonly WinoUnreadPill _pill = new();
    private readonly NSButton _action;
    private readonly WinoIconView _chevron = new(WinoIconGlyph.ChevronDown, 10, WinoStyle.SecondaryText);
    private readonly NSStackView _row;
    private IMenuItem? _item;
    private INotifyPropertyChanged? _observedParameter;

    public event Action<IMenuItem>? AttentionClicked;
    public event Action<IMenuItem>? ActionClicked;

    public ShellPaneCell(ShellPaneContext context)
    {
        _context = context;
        Identifier = ReuseIdentifier;

        _leadingWidth = _leadingBox.WidthAnchor.ConstraintEqualTo((nfloat)AccountIconSize);
        _leadingWidth.Active = true;
        _leadingBox.HeightAnchor.ConstraintEqualTo((nfloat)AccountIconSize).Active = true;
        _dot.CornerRadius = 10;
        _dotSize = _dot.WidthAnchor.ConstraintEqualTo(20);
        _dotSize.Active = true;
        _dot.HeightAnchor.ConstraintEqualTo(_dot.WidthAnchor).Active = true;
        _check.SetButtonType(NSButtonType.Switch);
        _check.Title = string.Empty;
        _check.ControlSize = NSControlSize.Small;
        _check.TranslatesAutoresizingMaskIntoConstraints = false;
        _check.Activated += CheckActivated;
        foreach (var view in new NSView[] { _glyph, _accountIcon, _dot, _check })
        {
            _leadingBox.AddSubview(view);
            NSLayoutConstraint.ActivateConstraints(
            [
                view.CenterXAnchor.ConstraintEqualTo(_leadingBox.CenterXAnchor),
                view.CenterYAnchor.ConstraintEqualTo(_leadingBox.CenterYAnchor)
            ]);
        }

        _title = WinoStyle.Label(string.Empty, WinoStyle.Body);
        _title.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        _titleChip.AddSubview(_title);
        _chipInsets =
        [
            _title.LeadingAnchor.ConstraintEqualTo(_titleChip.LeadingAnchor),
            _title.TrailingAnchor.ConstraintEqualTo(_titleChip.TrailingAnchor),
            _title.TopAnchor.ConstraintEqualTo(_titleChip.TopAnchor),
            _title.BottomAnchor.ConstraintEqualTo(_titleChip.BottomAnchor)
        ];
        NSLayoutConstraint.ActivateConstraints(_chipInsets);
        _titleChip.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        _subtitle = WinoStyle.Label(string.Empty, WinoStyle.Caption, WinoStyle.SecondaryText);
        _subtitle.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        // Only the name and address form the text block, so its centre is the centre of the two
        // lines; the sync bar hangs below it (see below) and never pushes the text upwards.
        var text = WinoLayout.VStack(1, _titleChip, _subtitle);
        text.Alignment = NSLayoutAttribute.Leading;
        text.DetachesHiddenViews = true;
        text.SetHuggingPriority(1, NSLayoutConstraintOrientation.Horizontal);
        // The block hugs its lines vertically so no spare height collects under the address.
        text.SetHuggingPriority(750, NSLayoutConstraintOrientation.Vertical);
        text.SetContentHuggingPriorityForOrientation(750, NSLayoutConstraintOrientation.Vertical);
        // The text column takes the remaining width (and truncates), so the unread pill and the
        // trailing accessories always hug their content at the trailing edge.
        text.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        text.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        _titleChip.WidthAnchor.ConstraintLessThanOrEqualTo(text.WidthAnchor).Active = true;
        _subtitle.WidthAnchor.ConstraintLessThanOrEqualTo(text.WidthAnchor).Active = true;

        _attention = IconButton(WinoIconGlyph.Warning, 18, 28, Translator.Info_AccountAttentionRequiredAction, Translator.Info_AccountAttentionRequiredClickableMessage);
        _attention.ContentTintColor = WinoStyle.Caution;
        _attention.Activated += (_, _) => { if (_item is { } item) AttentionClicked?.Invoke(item); };
        _action = IconButton(WinoIconGlyph.Folder, 16, 30, Translator.ToDoPage_NewGroup, Translator.ToDoPage_NewGroup);
        _action.Activated += (_, _) => { if (_item is { } item) ActionClicked?.Invoke(item); };
        _chevron.WidthAnchor.ConstraintEqualTo(12).Active = true;

        _row = WinoLayout.HStack(12, _leadingBox, text, _attention, _pill, _action, _chevron);
        _row.EdgeInsets = new NSEdgeInsets(0, AvatarInset, 0, 10);
        _row.Distribution = NSStackViewDistribution.Fill;
        _row.SetCustomSpacing((nfloat)IconTextSpacing, _leadingBox);
        _row.SetCustomSpacing(8, _pill);
        WinoLayout.Fill(_row, this);
        // The text block's centre is pinned to the icon column's centre (avatar, glyph or checkbox).
        text.CenterYAnchor.ConstraintEqualTo(_leadingBox.CenterYAnchor).Active = true;
        // Windows ProgressBar under the address (Margin 0,4,0,0): an overlay beneath the text block
        // that spans its width. Account rows are 50pt, leaving room for it below the centred lines.
        AddSubview(_sync);
        NSLayoutConstraint.ActivateConstraints(
        [
            _sync.LeadingAnchor.ConstraintEqualTo(text.LeadingAnchor),
            _sync.TrailingAnchor.ConstraintEqualTo(text.TrailingAnchor),
            _sync.TopAnchor.ConstraintEqualTo(text.BottomAnchor, 4)
        ]);
        TextField = _title;
    }

    private static NSButton IconButton(WinoIconGlyph glyph, double glyphSize, double size, string label, string toolTip)
    {
        var button = new NSButton
        {
            Bordered = false,
            Image = WinoIcons.Image(glyph, glyphSize, null, label),
            ImagePosition = NSCellImagePosition.ImageOnly,
            ToolTip = toolTip,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        button.SetButtonType(NSButtonType.MomentaryChange);
        WinoAccessibility.Label(button, label);
        WinoLayout.Size(button, size, size);
        return button;
    }

    public IMenuItem? Item => _item;

    /// <param name="selected">True while the row is the pane selection.</param>
    /// <param name="hasChildren">True when the outline can expand this row.</param>
    public void Configure(IMenuItem item, bool selected, bool hasChildren)
    {
        _item = item;
        ObserveParameter(null);
        Reset();
        bool enabled = ShellPaneRows.IsEnabled(item);
        AlphaValue = enabled ? 1 : 0.45f;
        _chevron.Hidden = !hasChildren;
        _chevron.Icon = item.IsExpanded ? WinoIconGlyph.ChevronDown : WinoIconGlyph.ChevronRight;
        bool bold = selected || item.IsSelected;

        switch (item)
        {
            case NewCalendarEventMenuItem:
                NewRow(WinoIconGlyph.NewMail, 18, Translator.CalendarEventCompose_NewEventButton);
                break;
            case NewMailMenuItem:
                NewRow(WinoIconGlyph.NewMail, 18, Translator.MenuNewMail);
                break;
            case NewContactMenuItem:
                NewRow(WinoIconGlyph.NewMail, 18, Translator.ContactsPane_NewContact);
                break;
            case NewAddressListMenuItem:
                NewRow(WinoIconGlyph.PersonAdd, 17, Translator.ContactList_NewTitle);
                break;
            case NewTaskListMenuItem:
                NewRow(WinoIconGlyph.Add, 18, Translator.ToDoPage_NewList);
                _action.Hidden = false;
                break;
            case MergedAccountMenuItem merged:
                Leading(AccountIconSize);
                _glyph.Hidden = false;
                _glyph.PointSize = 20;
                _glyph.Icon = WinoIconGlyph.PeopleLink;
                _glyph.Tint = null;
                SetTitle(merged.MergedAccountName, merged.IsChildSelected || bold ? WinoStyle.BodyStrong : WinoStyle.Body);
                SetSubtitle(merged.IsSynchronizationProgressVisible && !string.IsNullOrWhiteSpace(merged.SynchronizationStatus)
                    ? merged.SynchronizationStatus
                    : merged.MergedAccountCount + Translator.MenuMergedAccountItemAccountsSuffix);
                SetSync(merged.IsSynchronizationProgressVisible, merged.IsProgressIndeterminate, merged.SynchronizationProgressValue);
                _pill.Count = merged.UnreadItemCount;
                break;
            case IAccountNavigationMenuItem account when ShellPaneRows.IsAccountRow(account):
                Leading(AccountIconSize);
                _accountIcon.Hidden = false;
                _accountIcon.Account = account.Account is { } mailAccount ? _context.AccountIcon(mailAccount) : null;
                SetTitle(account.AccountName, account.IsSelected ? WinoStyle.BodyStrong : WinoStyle.Body);
                SetSubtitle(account.AccountAddress);
                SetSync(account.IsSynchronizationProgressVisible, account.IsProgressIndeterminate, account.SynchronizationProgressValue);
                _pill.Count = account.UnreadItemCount;
                _attention.Hidden = !account.IsAttentionRequired;
                break;
            case MailCategoryMenuItem category:
                GlyphRow(WinoIconGlyph.Tag, 16, WinoStyle.FromHexString(category.BackgroundColorHex) ?? WinoStyle.FromHexString(category.TextColorHex));
                SetTitle(category.FolderName, bold ? WinoStyle.BodyStrong : WinoStyle.Body);
                _pill.Count = category.ShowUnreadCount ? category.UnreadItemCount : 0;
                break;
            case MergedMailCategoryMenuItem mergedCategory:
                GlyphRow(WinoIconGlyph.Tag, 16, WinoStyle.FromHexString(mergedCategory.TextColorHex));
                SetTitle(mergedCategory.FolderName, bold ? WinoStyle.BodyStrong : WinoStyle.Body, WinoStyle.FromHexString(mergedCategory.TextColorHex));
                _pill.Count = mergedCategory.UnreadItemCount;
                break;
            case MergedAccountMoreFolderMenuItem:
                GlyphRow(WinoIconGlyph.SpecialFolderMore, 16, null);
                SetTitle(Translator.More, bold ? WinoStyle.BodyStrong : WinoStyle.Body);
                break;
            case FolderMenuItem { HasTextColor: true } coloured:
                GlyphRow(ShellPaneRows.FolderGlyph(coloured.SpecialFolderType), 16, null);
                SetTitle(coloured.FolderName, bold ? WinoStyle.BodyStrong : WinoStyle.Body, WinoStyle.FromHexString(coloured.Parameter?.TextColorHex),
                    WinoStyle.FromHexString(coloured.Parameter?.BackgroundColorHex));
                _pill.Count = coloured.ShowUnreadCount ? coloured.UnreadItemCount : 0;
                break;
            case IBaseFolderMenuItem folder:
                GlyphRow(ShellPaneRows.FolderGlyph(folder.SpecialFolderType), 16, null);
                SetTitle(folder.FolderName, bold ? WinoStyle.BodyStrong : WinoStyle.Body);
                _pill.Count = folder.ShowUnreadCount ? folder.UnreadItemCount : 0;
                break;
            case UngroupedCalendarMenuItem calendar:
                Leading(20);
                _check.Hidden = false;
                _check.State = calendar.Parameter.IsChecked ? NSCellStateValue.On : NSCellStateValue.Off;
                _dot.Hidden = false;
                _dotSize.Constant = 16;
                _dot.CornerRadius = 8;
                _dot.Fill = WinoStyle.FromHexString(calendar.Parameter.BackgroundColorHex) ?? WinoStyle.Accent;
                MoveDotBesideCheck();
                SetTitle(calendar.Parameter.Name, WinoStyle.Body);
                ObserveParameter(calendar.Parameter);
                break;
            case ContactCategoriesExpanderMenuItem expander:
                GlyphRow(WinoIconGlyph.Tag, 16, null);
                SetTitle(expander.Title, WinoStyle.Body);
                _chevron.Hidden = false;
                _chevron.Icon = expander.IsExpanded ? WinoIconGlyph.ChevronDown : WinoIconGlyph.ChevronRight;
                break;
            case ContactFilterViewModel { IsCategory: true } categoryFilter:
                GlyphRow(WinoIconGlyph.Tag, 16, WinoStyle.FromHexString(categoryFilter.CategoryColorHex));
                SetTitle(categoryFilter.Name, bold ? WinoStyle.BodyStrong : WinoStyle.Body);
                _pill.Count = categoryFilter.Count;
                break;
            case ContactFilterViewModel filter:
                if (filter.Account is { } filterAccount)
                {
                    Leading(20);
                    _accountIcon.Hidden = false;
                    _accountIcon.Size = 20;
                    _accountIcon.Account = _context.AccountIcon(filterAccount);
                }
                else
                {
                    Leading(20);
                    _glyph.Hidden = false;
                    _glyph.PointSize = 15;
                    _glyph.Glyph = filter.Glyph ?? string.Empty;
                    _glyph.Tint = null;
                }
                SetTitle(filter.Name, bold ? WinoStyle.BodyStrong : WinoStyle.Body);
                _pill.Count = filter.Count;
                break;
            case MyDayTaskMenuItem myDay:
                GlyphRow(WinoIconGlyph.WeatherSunny, 16, WinoStyle.Accent);
                SetTitle(Translator.ToDoPage_MyDay, bold ? WinoStyle.BodyStrong : WinoStyle.Body);
                _pill.Count = myDay.Count;
                break;
            case PlannedTaskMenuItem planned:
                GlyphRow(WinoIconGlyph.Calendar, 16, null);
                SetTitle(Translator.ToDoPage_Planned, bold ? WinoStyle.BodyStrong : WinoStyle.Body);
                _pill.Count = planned.Count;
                break;
            case ImportantTaskMenuItem important:
                GlyphRow(WinoIconGlyph.Star, 16, null);
                SetTitle(Translator.ToDoPage_Important, bold ? WinoStyle.BodyStrong : WinoStyle.Body);
                _pill.Count = important.Count;
                break;
            case AccountTaskListGroupMenuItem group:
                GlyphRow(WinoIconGlyph.PanelLeft, 16, null);
                SetTitle(group.Title, WinoStyle.Body);
                break;
            case AccountTaskListMenuItem list:
                GlyphRow(WinoIconGlyph.Dot, 20, WinoStyle.FromHexString(list.ColorHex));
                SetTitle(list.Title, bold ? WinoStyle.BodyStrong : WinoStyle.Body);
                _pill.Count = list.Count;
                break;
            case RateMenuItem:
                GlyphRow(WinoIconGlyph.Heart, 16, null);
                SetTitle(Translator.MenuRate, WinoStyle.Body);
                break;
            case SettingsItem:
                GlyphRow(WinoIconGlyph.Settings, 16, null);
                SetTitle(Translator.MenuSettings, WinoStyle.Body);
                break;
            default:
                GlyphRow(WinoIconGlyph.Folder, 16, null);
                SetTitle(item.ToString() ?? string.Empty, WinoStyle.Body);
                break;
        }
        AccessibilityLabel = _pill.Hidden ? _title.StringValue : $"{_title.StringValue}, {_pill.AccessibilityLabel}";
    }

    private void Reset()
    {
        _glyph.Hidden = _accountIcon.Hidden = _dot.Hidden = _check.Hidden = true;
        _accountIcon.Size = AccountIconSize;
        _dotSize.Constant = 20;
        _dot.CornerRadius = 10;
        _subtitle.Hidden = true;
        _sync.Hidden = true;
        _attention.Hidden = true;
        _action.Hidden = true;
        _pill.Count = 0;
        _title.TextColor = WinoStyle.PrimaryText;
        _titleChip.Fill = null;
        foreach (var inset in _chipInsets) inset.Constant = 0;
        _row.EdgeInsets = new NSEdgeInsets(0, AvatarInset, 0, 10);
        if (_dotTrailing is not null) { _dotTrailing.Active = false; _dotTrailing = null; }
    }

    private NSLayoutConstraint? _dotTrailing;

    private void MoveDotBesideCheck()
    {
        // Calendar rows: checkbox, colour dot, name (Windows UngroupedCalendarTemplate).
        _leadingWidth.Constant = 20 + 8 + 16;
        _dotTrailing = _dot.TrailingAnchor.ConstraintEqualTo(_leadingBox.TrailingAnchor);
        _dotTrailing.Active = true;
    }

    private void NewRow(WinoIconGlyph glyph, double size, string title)
    {
        // Shares the account rows' icon column so the glyph and the avatars line up.
        GlyphRow(glyph, size, null);
        Leading(AccountIconSize);
        SetTitle(title, WinoStyle.BodyStrong);
    }

    private void Leading(double width)
    {
        _leadingWidth.Constant = (nfloat)width;
        _row.EdgeInsets = new NSEdgeInsets(0, width >= AccountIconSize ? AvatarInset : GlyphInset, 0, 10);
    }

    private void GlyphRow(WinoIconGlyph glyph, double size, NSColor? tint)
    {
        Leading(20);
        _glyph.Hidden = false;
        _glyph.PointSize = size;
        _glyph.Icon = glyph;
        _glyph.Tint = tint;
    }

    private void SetTitle(string? text, NSFont font, NSColor? color = null, NSColor? chipFill = null)
    {
        _title.StringValue = text ?? string.Empty;
        _title.Font = font;
        _title.TextColor = color ?? WinoStyle.PrimaryText;
        _titleChip.Fill = chipFill;
        if (chipFill is null) return;
        _chipInsets[0].Constant = 4; _chipInsets[1].Constant = -4; _chipInsets[2].Constant = 1; _chipInsets[3].Constant = -1;
    }

    private void SetSubtitle(string? text)
    {
        _subtitle.StringValue = text ?? string.Empty;
        _subtitle.Hidden = string.IsNullOrWhiteSpace(text);
    }

    private void SetSync(bool visible, bool indeterminate, double value)
    {
        _sync.Hidden = !visible;
        if (visible) _sync.Update(indeterminate, value);
    }

    private void CheckActivated(object? sender, EventArgs args)
    {
        if (_item is UngroupedCalendarMenuItem calendar) calendar.Parameter.IsChecked = _check.State == NSCellStateValue.On;
    }

    private void ObserveParameter(INotifyPropertyChanged? parameter)
    {
        if (_observedParameter is not null) _observedParameter.PropertyChanged -= ParameterChanged;
        _observedParameter = parameter;
        if (parameter is not null) parameter.PropertyChanged += ParameterChanged;
    }

    private void ParameterChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (sender is AccountCalendarViewModel calendar && args.PropertyName is nameof(AccountCalendarViewModel.IsChecked))
            NSApplication.SharedApplication.BeginInvokeOnMainThread(() => _check.State = calendar.IsChecked ? NSCellStateValue.On : NSCellStateValue.Off);
    }

    /// <summary>True when the point (in cell coordinates) is over the trailing chevron area.</summary>
    public bool IsInChevronArea(CGPoint point) => !_chevron.Hidden && point.X >= Bounds.Width - 34;

    protected override void Dispose(bool disposing)
    {
        if (disposing) ObserveParameter(null);
        base.Dispose(disposing);
    }
}

/// <summary>Section caption between groups (Windows ShellSectionHeaderTemplate: 11pt tertiary), aligned with the row glyphs.</summary>
internal sealed class ShellSectionHeaderCell : NSTableCellView
{
    public const string ReuseIdentifier = "WinoShellSectionHeader";
    private readonly NSTextField _label = WinoStyle.Label(string.Empty, WinoStyle.Caption, WinoStyle.TertiaryText);

    public ShellSectionHeaderCell()
    {
        Identifier = ReuseIdentifier;
        AddSubview(_label);
        TextField = _label;
        NSLayoutConstraint.ActivateConstraints(
        [
            _label.LeadingAnchor.ConstraintEqualTo(LeadingAnchor, 16),
            _label.TrailingAnchor.ConstraintLessThanOrEqualTo(TrailingAnchor, -12),
            _label.BottomAnchor.ConstraintEqualTo(BottomAnchor, -3)
        ]);
    }

    public void Configure(string title)
    {
        _label.StringValue = title;
        AccessibilityLabel = title;
    }
}

/// <summary>Windows NavigationViewItemSeparator: a hairline across the pane.</summary>
internal sealed class ShellSeparatorCell : NSTableCellView
{
    public const string ReuseIdentifier = "WinoShellSeparator";

    public ShellSeparatorCell()
    {
        Identifier = ReuseIdentifier;
        var line = new WinoSeparator();
        AddSubview(line);
        NSLayoutConstraint.ActivateConstraints(
        [
            line.LeadingAnchor.ConstraintEqualTo(LeadingAnchor, 10),
            line.TrailingAnchor.ConstraintEqualTo(TrailingAnchor, -10),
            line.CenterYAnchor.ConstraintEqualTo(CenterYAnchor)
        ]);
        AccessibilityElement = false;
    }
}

/// <summary>The "fix account" row: warning text centred in the pane, click to repair.</summary>
internal sealed class ShellFixAccountCell : NSTableCellView
{
    public const string ReuseIdentifier = "WinoShellFixAccount";
    private readonly NSTextField _label = WinoStyle.Label(string.Empty, WinoStyle.Description, WinoStyle.Caution, maximumLines: 0);

    public ShellFixAccountCell()
    {
        Identifier = ReuseIdentifier;
        _label.Alignment = NSTextAlignment.Center;
        AddSubview(_label);
        TextField = _label;
        NSLayoutConstraint.ActivateConstraints(
        [
            _label.LeadingAnchor.ConstraintEqualTo(LeadingAnchor, 16),
            _label.TrailingAnchor.ConstraintEqualTo(TrailingAnchor, -16),
            _label.CenterYAnchor.ConstraintEqualTo(CenterYAnchor)
        ]);
    }

    // Windows shows these rows with literal English text as well (ShellMenuTemplates.xaml).
    public void Configure(FixAccountIssuesMenuItem item)
    {
        _label.StringValue = item.Account?.AttentionReason == AccountAttentionReason.MissingSystemFolderConfiguration
            ? "Account is missing system folder configuration.\nClick here to fix it."
            : "Account credentials can not be verified.\nClick here to fix it.";
        AccessibilityLabel = _label.StringValue;
    }
}

/// <summary>The calendar pane's visible-range picker bound to the calendar shell client.</summary>
internal sealed class ShellDatePickerCell : NSTableCellView
{
    public const string ReuseIdentifier = "WinoShellDatePicker";
    private readonly ShellPaneContext _context;
    private readonly WinoMiniCalendarView _calendar = new();
    private CalendarDatePickerMenuItem? _item;
    private ICalendarShellClient? _client;

    public ShellDatePickerCell(ShellPaneContext context)
    {
        _context = context;
        Identifier = ReuseIdentifier;
        WinoLayout.Fill(_calendar, this, 2, 12, 8, 12);
        _calendar.DateClicked += DateClicked;
        _calendar.ToggleClicked += (_, _) => { if (_item is { } item) item.IsCalendarExpanded = !item.IsCalendarExpanded; };
    }

    public void Configure(CalendarDatePickerMenuItem item)
    {
        _item = item;
        Observe(item.Parameter);
        _calendar.FirstDayOfWeek = _context.FirstDayOfWeek();
        _calendar.Collapsed = !item.IsCalendarExpanded;
        _calendar.SetToggle(item.IsCalendarExpanded ? WinoIconGlyph.PanelLeftContract : WinoIconGlyph.PanelLeftExpand, item.ExpansionToolTip);
        Synchronize();
        AccessibilityLabel = item.ExpansionAutomationName;
    }

    private void Observe(ICalendarShellClient? client)
    {
        if (ReferenceEquals(_client, client)) return;
        if (_client is not null) _client.PropertyChanged -= ClientChanged;
        _client = client;
        if (client is not null) client.PropertyChanged += ClientChanged;
    }

    private void ClientChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not (null or "" or nameof(ICalendarShellClient.CurrentVisibleRange) or nameof(ICalendarShellClient.VisibleDateRangeText))) return;
        NSApplication.SharedApplication.BeginInvokeOnMainThread(Synchronize);
    }

    private void Synchronize()
    {
        if (_client?.CurrentVisibleRange is not { } range) { _calendar.SelectedDates = new HashSet<DateOnly>(); return; }
        _calendar.SelectedDates = new HashSet<DateOnly>(range.Dates);
        _calendar.DisplayMonth = range.AnchorDate;
    }

    private void DateClicked(object? sender, DateOnly date)
    {
        if (_client is null) return;
        var args = new CalendarViewDayClickedEventArgs(date.ToDateTime(TimeOnly.MinValue));
        if (_client.DateClickedCommand.CanExecute(args)) _client.DateClickedCommand.Execute(args);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Observe(null);
        base.Dispose(disposing);
    }
}

/// <summary>
/// One account's calendars (Windows AccountCalendarGroupTemplate): a header with a three-state
/// checkbox, the account colour, name and address, then a checkbox row per calendar while expanded.
/// </summary>
internal sealed class ShellCalendarGroupCell : NSTableCellView
{
    public const string ReuseIdentifier = "WinoShellCalendarGroup";
    public const double HeaderHeight = 46;
    public const double CalendarRowHeight = 30;

    private readonly NSButton _check = WinoCheckbox.Create(null);
    private readonly WinoSurfaceView _dot = new() { CornerRadius = 6 };
    private readonly NSTextField _name = WinoStyle.Label(string.Empty, NSFont.SystemFontOfSize(12, NSFontWeight.Semibold));
    private readonly NSTextField _address = WinoStyle.Label(string.Empty, WinoStyle.Caption, WinoStyle.SecondaryText);
    private readonly NSTextField _status = WinoStyle.Label(string.Empty, WinoStyle.Caption, WinoStyle.SecondaryText);
    private readonly WinoSyncBar _sync = new();
    private readonly WinoIconView _chevron = new(WinoIconGlyph.ChevronDown, 10, WinoStyle.SecondaryText);
    private readonly NSStackView _calendars;
    private readonly NSView _header;
    private readonly List<(AccountCalendarViewModel Calendar, NSButton Check)> _rows = new();
    private GroupedAccountCalendarViewModel? _group;
    private bool _applying;

    /// <summary>Raised when the expanded state or calendar count changes and the row needs a new height.</summary>
    public event Action<ShellCalendarGroupCell>? HeightChanged;

    public ShellCalendarGroupCell()
    {
        Identifier = ReuseIdentifier;
        _check.SetButtonType(NSButtonType.Switch);
        _check.Title = string.Empty;
        _check.AllowsMixedState = true;
        _check.ControlSize = NSControlSize.Small;
        _check.TranslatesAutoresizingMaskIntoConstraints = false;
        _check.Activated += (_, _) => { if (_group is { } group && !_applying) group.IsCheckedState = _check.State == NSCellStateValue.On; };
        WinoLayout.Size(_dot, 12, 12);
        // Two lines (name, then address or the sync status while syncing) centred on the checkbox
        // and colour dot; the sync bar hangs below them so it never shifts the text.
        var text = WinoLayout.VStack(1, _name, _address, _status);
        text.DetachesHiddenViews = true;
        text.SetHuggingPriority(1, NSLayoutConstraintOrientation.Horizontal);
        text.SetHuggingPriority(750, NSLayoutConstraintOrientation.Vertical);
        text.SetContentHuggingPriorityForOrientation(750, NSLayoutConstraintOrientation.Vertical);
        _name.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        _address.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        _status.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        _chevron.WidthAnchor.ConstraintEqualTo(12).Active = true;
        var header = WinoLayout.HStack(10, _check, _dot, text, _chevron);
        header.EdgeInsets = new NSEdgeInsets(0, 14, 0, 10);
        header.HeightAnchor.ConstraintEqualTo((nfloat)HeaderHeight).Active = true;
        text.CenterYAnchor.ConstraintEqualTo(_dot.CenterYAnchor).Active = true;
        header.AddSubview(_sync);
        NSLayoutConstraint.ActivateConstraints(
        [
            _sync.LeadingAnchor.ConstraintEqualTo(text.LeadingAnchor),
            _sync.TrailingAnchor.ConstraintEqualTo(text.TrailingAnchor),
            _sync.TopAnchor.ConstraintEqualTo(text.BottomAnchor, 3)
        ]);
        _header = header;
        _calendars = WinoLayout.VStack(0);
        _calendars.Alignment = NSLayoutAttribute.Leading;
        var column = WinoLayout.VStack(0, header, _calendars);
        column.Alignment = NSLayoutAttribute.Leading;
        header.WidthAnchor.ConstraintEqualTo(column.WidthAnchor).Active = true;
        _calendars.WidthAnchor.ConstraintEqualTo(column.WidthAnchor).Active = true;
        AddSubview(column);
        NSLayoutConstraint.ActivateConstraints(
        [
            column.LeadingAnchor.ConstraintEqualTo(LeadingAnchor),
            column.TrailingAnchor.ConstraintEqualTo(TrailingAnchor),
            column.TopAnchor.ConstraintEqualTo(TopAnchor, 2)
        ]);
        TextField = _name;
    }

    public void Configure(AccountCalendarGroupMenuItem item)
    {
        Observe(item.Parameter);
        Apply();
    }

    private void Observe(GroupedAccountCalendarViewModel? group)
    {
        if (ReferenceEquals(_group, group)) return;
        if (_group is not null)
        {
            _group.PropertyChanged -= GroupChanged;
            _group.AccountCalendars.CollectionChanged -= CalendarsChanged;
        }
        _group = group;
        if (group is not null)
        {
            group.PropertyChanged += GroupChanged;
            group.AccountCalendars.CollectionChanged += CalendarsChanged;
        }
    }

    private void GroupChanged(object? sender, PropertyChangedEventArgs args)
        => NSApplication.SharedApplication.BeginInvokeOnMainThread(() =>
        {
            Apply();
            if (args.PropertyName is nameof(GroupedAccountCalendarViewModel.IsExpanded)) HeightChanged?.Invoke(this);
        });

    private void CalendarsChanged(object? sender, NotifyCollectionChangedEventArgs args)
        => NSApplication.SharedApplication.BeginInvokeOnMainThread(() => { Apply(); HeightChanged?.Invoke(this); });

    private void Apply()
    {
        if (_group is not { } group) return;
        _applying = true;
        try
        {
            _check.State = group.IsCheckedState switch { true => NSCellStateValue.On, false => NSCellStateValue.Off, null => NSCellStateValue.Mixed };
            _dot.Fill = WinoStyle.FromHexString(group.AccountColorHex) ?? WinoStyle.AvatarColor(group.Account?.Name);
            _name.StringValue = group.Account?.Name ?? string.Empty;
            _address.StringValue = group.Account?.Address ?? string.Empty;
            _status.StringValue = group.SynchronizationStatus ?? string.Empty;
            _status.Hidden = !group.IsSynchronizationProgressVisible || string.IsNullOrWhiteSpace(_status.StringValue);
            // The status takes the address line while syncing so the header keeps two centred lines.
            _address.Hidden = !_status.Hidden || string.IsNullOrWhiteSpace(_address.StringValue);
            _sync.Hidden = !group.IsSynchronizationProgressVisible;
            if (group.IsSynchronizationProgressVisible) _sync.Update(group.IsProgressIndeterminate, group.SynchronizationProgressValue);
            _chevron.Icon = group.IsExpanded ? WinoIconGlyph.ChevronDown : WinoIconGlyph.ChevronRight;
            // Progress and status ticks arrive often; only expansion or membership changes rebuild the rows.
            if (!RowsMatch(group)) RebuildCalendars(group);
            AccessibilityLabel = _name.StringValue;
        }
        finally { _applying = false; }
    }

    private bool RowsMatch(GroupedAccountCalendarViewModel group)
    {
        if (!group.IsExpanded) return _rows.Count == 0;
        var calendars = group.AccountCalendars;
        if (_rows.Count != calendars.Count) return false;
        for (int i = 0; i < _rows.Count; i++)
            if (!ReferenceEquals(_rows[i].Calendar, calendars[i])) return false;
        return true;
    }

    private void RebuildCalendars(GroupedAccountCalendarViewModel group)
    {
        foreach (var (calendar, _) in _rows) calendar.PropertyChanged -= CalendarChanged;
        _rows.Clear();
        foreach (var view in _calendars.ArrangedSubviews) { _calendars.RemoveArrangedSubview(view); view.RemoveFromSuperview(); }
        if (!group.IsExpanded) return;
        foreach (var calendar in group.AccountCalendars)
        {
            var check = WinoCheckbox.Create(null); check.ControlSize = NSControlSize.Small;
            check.SetButtonType(NSButtonType.Switch);
            check.State = calendar.IsChecked ? NSCellStateValue.On : NSCellStateValue.Off;
            var captured = calendar;
            check.Activated += (_, _) => captured.IsChecked = check.State == NSCellStateValue.On;
            WinoAccessibility.Label(check, calendar.Name);
            var dot = new WinoSurfaceView { CornerRadius = 6, Fill = WinoStyle.FromHexString(calendar.BackgroundColorHex) ?? WinoStyle.Accent };
            WinoLayout.Size(dot, 12, 12);
            var name = WinoStyle.Label(calendar.Name, NSFont.SystemFontOfSize(12));
            name.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
            var row = WinoLayout.HStack(10, check, dot, name);
            // The calendar checkbox sits under the account colour dot of the header.
            row.EdgeInsets = new NSEdgeInsets(0, 38, 0, 10);
            row.HeightAnchor.ConstraintEqualTo((nfloat)CalendarRowHeight).Active = true;
            _calendars.AddArrangedSubview(row);
            row.WidthAnchor.ConstraintEqualTo(_calendars.WidthAnchor).Active = true;
            calendar.PropertyChanged += CalendarChanged;
            _rows.Add((calendar, check));
        }
    }

    private void CalendarChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not nameof(AccountCalendarViewModel.IsChecked)) return;
        NSApplication.SharedApplication.BeginInvokeOnMainThread(() =>
        {
            foreach (var (calendar, check) in _rows) check.State = calendar.IsChecked ? NSCellStateValue.On : NSCellStateValue.Off;
        });
    }

    /// <summary>Clicks on the header (outside the checkbox) expand or collapse the group.</summary>
    public override void MouseDown(NSEvent theEvent)
    {
        var point = ConvertPointFromView(theEvent.LocationInWindow, null);
        var inHeader = _header.Frame.Contains(point);
        if (_group is { } group && inHeader && !_check.Frame.Contains(_header.ConvertPointFromView(theEvent.LocationInWindow, null)))
        {
            group.IsExpanded = !group.IsExpanded;
            return;
        }
        base.MouseDown(theEvent);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var (calendar, _) in _rows) calendar.PropertyChanged -= CalendarChanged;
            _rows.Clear();
            Observe(null);
        }
        base.Dispose(disposing);
    }
}
