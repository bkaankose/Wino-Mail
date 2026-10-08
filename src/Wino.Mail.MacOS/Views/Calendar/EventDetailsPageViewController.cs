using System.Text;
using AppKit;
using Foundation;
using Wino.Calendar.ViewModels;
using Wino.Calendar.ViewModels.Data;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.AppKit.Common;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Calendar;

/// <summary>
/// Event details (Windows EventDetailsPage) in the calendar page's 320pt details zone: an action
/// bar (Join, RSVP, Delete, Edit series, Show as, Reminder), the RSVP panel, the title with the
/// calendar colour, detail rows with Wino glyphs, the people card with RSVP glyphs, the
/// description and attachments. Driven by the shared EventDetailsPageViewModel.
/// </summary>
public sealed class EventDetailsPageViewController(EventDetailsPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger)
    : WinoViewController<EventDetailsPageViewModel>(viewModel, dispatcher, logger)
{
    private static readonly NSColor CardFill = WinoStyle.Dynamic(WinoStyle.Hex(0xF7F7F8), WinoStyle.Hex(0xFFFFFF, 0.06));

    private NSStackView _column = null!;
    private WinoInfoBar _readOnlyBar = null!;
    private bool _isReadOnly;
    private ActionBarButton _join = null!;
    private ActionBarButton _rsvp = null!;
    private ActionBarButton _delete = null!;
    private ActionBarButton _series = null!;
    private NSPopUpButton _showAs = null!;
    private NSPopUpButton _reminder = null!;
    private WinoSurfaceView _rsvpPanel = null!;
    private NSTextField _rsvpMessage = null!;
    private WinoSurfaceView _colorSwatch = null!;
    private NSTextField _title = null!;
    private DetailRow _when = null!;
    private DetailRow _location = null!;
    private DetailRow _online = null!;
    private DetailRow _repeat = null!;
    private DetailRow _reminderRow = null!;
    private DetailRow _calendar = null!;
    private WinoSurfaceView _peopleCard = null!;
    private NSTextField _peopleTitle = null!;
    private NSStackView _peopleList = null!;
    private NSTextView _description = null!;
    private NSScrollView _descriptionScroll = null!;
    private WinoSurfaceView _attachmentsCard = null!;
    private NSStackView _attachmentList = null!;
    private BindingScope? _eventBindings;

    public override void LoadView()
    {
        _column = WinoLayout.VStack(12);
        _column.EdgeInsets = new NSEdgeInsets(16, 16, 16, 16);

        // Read-only calendars (subscriptions, birthdays, shared without write access) keep the event
        // viewable but drop the actions that would change it.
        _readOnlyBar = new WinoInfoBar(WinoInfoBarSeverity.Warning, Translator.CalendarReadOnly_Title, Translator.CalendarReadOnly_Message)
        {
            IsClosable = false,
            Hidden = true
        };
        AddRow(_readOnlyBar);

        // Action bar
        _join = Action(WinoIconGlyph.EventJoinOnline, Translator.CalendarEventDetails_JoinOnline, ViewModel.JoinOnlineCommand);
        _rsvp = Action(WinoIconGlyph.EventRespond, Translator.CalendarEventResponse_NotResponded, ViewModel.ToggleRsvpPanelCommand);
        _delete = Action(WinoIconGlyph.Delete, Translator.Buttons_Delete, ViewModel.DeleteCommand);
        _series = Action(WinoIconGlyph.EventEditSeries, Translator.CalendarEventDetails_EditSeries, ViewModel.ViewSeriesCommand);
        _showAs = new NSPopUpButton { ControlSize = NSControlSize.Small, TranslatesAutoresizingMaskIntoConstraints = false, ToolTip = Translator.CalendarEventDetails_ShowAs };
        _showAs.Font = NSFont.SystemFontOfSize(12);
        _showAs.WidthAnchor.ConstraintEqualTo(130).Active = true;
        foreach (var option in ViewModel.ShowAsOptions) _showAs.AddItem(option.DisplayText);
        _showAs.Activated += (_, _) =>
        {
            var index = (int)_showAs.IndexOfSelectedItem;
            if (index >= 0 && index < ViewModel.ShowAsOptions.Count) ViewModel.SelectedShowAsOption = ViewModel.ShowAsOptions[index];
        };
        var showAsRow = WinoLayout.HStack(6, new WinoIconView(WinoIconGlyph.CalendarShowAs, 16, WinoStyle.SecondaryText), _showAs);
        _reminder = new NSPopUpButton { PullsDown = true, ControlSize = NSControlSize.Small, TranslatesAutoresizingMaskIntoConstraints = false, ToolTip = Translator.CalendarEventDetails_Reminder };
        _reminder.Font = NSFont.SystemFontOfSize(12);
        _reminder.AddItem(Translator.CalendarEventDetails_Reminder);
        foreach (var option in ViewModel.ReminderOptions)
        {
            var item = new NSMenuItem(option.DisplayText) { State = option.IsSelected ? NSCellStateValue.On : NSCellStateValue.Off };
            var captured = option;
            item.Activated += (_, _) => { captured.IsSelected = !captured.IsSelected; item.State = captured.IsSelected ? NSCellStateValue.On : NSCellStateValue.Off; UpdateReminderRow(); };
            _reminder.Menu!.AddItem(item);
        }
        var reminderRow = WinoLayout.HStack(6, new WinoIconView(WinoIconGlyph.Reminder, 16, WinoStyle.SecondaryText), _reminder);
        var actions = new NSStackView { Orientation = NSUserInterfaceLayoutOrientation.Horizontal, Spacing = 2, TranslatesAutoresizingMaskIntoConstraints = false };
        actions.Alignment = NSLayoutAttribute.CenterY;
        foreach (var view in new NSView[] { _join, _rsvp, _delete, _series }) actions.AddArrangedSubview(view);
        var secondary = WinoLayout.HStack(10, showAsRow, reminderRow, WinoLayout.Spacer());
        var actionColumn = WinoLayout.VStack(4, actions, secondary);
        actionColumn.EdgeInsets = new NSEdgeInsets(6, 6, 6, 6);
        var actionCard = new WinoSurfaceView { Fill = CardFill, CornerRadius = 7 };
        WinoLayout.Fill(actionColumn, actionCard);
        AddRow(actionCard);

        // RSVP panel
        _rsvpPanel = new WinoSurfaceView { Fill = CardFill, Stroke = WinoStyle.ZoneStroke, CornerRadius = 7, Hidden = true };
        var accept = RsvpButton(WinoIconGlyph.EventAccept, WinoStyle.Success, Translator.CalendarEventRsvpPanel_Accept, AttendeeStatus.Accepted);
        var tentative = RsvpButton(WinoIconGlyph.EventTentative, WinoStyle.Caution, Translator.CalendarEventRsvpPanel_Tentative, AttendeeStatus.Tentative);
        var decline = RsvpButton(WinoIconGlyph.EventDecline, WinoStyle.Critical, Translator.CalendarEventRsvpPanel_Decline, AttendeeStatus.Declined);
        var close = new ActionBarButton(WinoIconGlyph.Dismiss, null, Translator.Buttons_Close);
        close.Activated += (_, _) => ViewModel.CloseRsvpPanelCommand.Execute(null);
        var rsvpButtons = WinoLayout.HStack(2, accept, tentative, decline, WinoLayout.Spacer(), close);
        _rsvpMessage = new NSTextField { PlaceholderString = Translator.CalendarEventRsvpPanel_AddMessage, TranslatesAutoresizingMaskIntoConstraints = false, Font = NSFont.SystemFontOfSize(12) };
        _rsvpMessage.Changed += (_, _) => ViewModel.RsvpMessage = _rsvpMessage.StringValue;
        var rsvpColumn = WinoLayout.VStack(8, rsvpButtons, _rsvpMessage);
        rsvpColumn.EdgeInsets = new NSEdgeInsets(10, 10, 10, 10);
        rsvpButtons.WidthAnchor.ConstraintEqualTo(rsvpColumn.WidthAnchor, 1, -20).Active = true;
        _rsvpMessage.WidthAnchor.ConstraintEqualTo(rsvpColumn.WidthAnchor, 1, -20).Active = true;
        WinoLayout.Fill(rsvpColumn, _rsvpPanel);
        AddRow(_rsvpPanel);

        // Title
        _colorSwatch = new WinoSurfaceView { CornerRadius = 3 };
        WinoLayout.Size(_colorSwatch, 12, 12);
        _title = WinoStyle.Label(string.Empty, NSFont.SystemFontOfSize(16, NSFontWeight.Semibold), WinoStyle.PrimaryText, 3);
        var titleRow = WinoLayout.HStack(10, _colorSwatch, _title);
        titleRow.Alignment = NSLayoutAttribute.Top;
        AddRow(titleRow);

        // Detail rows
        _when = new DetailRow(WinoIconGlyph.Clock);
        _location = new DetailRow(WinoIconGlyph.Location);
        _online = new DetailRow(WinoIconGlyph.EventJoinOnline);
        _repeat = new DetailRow(WinoIconGlyph.CalendarEventRepeat);
        _reminderRow = new DetailRow(WinoIconGlyph.Reminder);
        _calendar = new DetailRow(WinoIconGlyph.Calendar);
        var details = WinoLayout.VStack(8, _when, _location, _online, _repeat, _reminderRow, _calendar);
        details.DetachesHiddenViews = true;
        AddRow(details);

        // People
        _peopleTitle = WinoStyle.Label(Translator.CalendarEventDetails_People, WinoStyle.Caption, WinoStyle.SecondaryText);
        _peopleList = WinoLayout.VStack(2);
        var peopleColumn = WinoLayout.VStack(8, _peopleTitle, _peopleList);
        peopleColumn.EdgeInsets = new NSEdgeInsets(10, 12, 10, 12);
        _peopleList.WidthAnchor.ConstraintEqualTo(peopleColumn.WidthAnchor, 1, -24).Active = true;
        _peopleCard = new WinoSurfaceView { Fill = CardFill, CornerRadius = 4, Hidden = true };
        WinoLayout.Fill(peopleColumn, _peopleCard);
        AddRow(_peopleCard);

        // Description
        _description = new NSTextView { Editable = false, Selectable = true, DrawsBackground = false, BackgroundColor = NSColor.Clear, TextContainerInset = new CoreGraphics.CGSize(0, 0) };
        _description.TextContainer!.WidthTracksTextView = true;
        _description.VerticallyResizable = true;
        _description.HorizontallyResizable = false;
        _description.AutoresizingMask = NSViewResizingMask.WidthSizable;
        _descriptionScroll = new NSScrollView { DocumentView = _description, DrawsBackground = false, HasVerticalScroller = true, AutohidesScrollers = true, BorderType = NSBorderType.NoBorder, TranslatesAutoresizingMaskIntoConstraints = false, Hidden = true };
        _descriptionScroll.HeightAnchor.ConstraintGreaterThanOrEqualTo(80).Active = true;
        _descriptionScroll.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Vertical);
        AddRow(_descriptionScroll);

        // Attachments
        var attachmentsTitle = WinoStyle.Label(Translator.CalendarEventDetails_Attachments, WinoStyle.Caption, WinoStyle.SecondaryText);
        _attachmentList = WinoLayout.VStack(2);
        var attachmentsColumn = WinoLayout.VStack(8, attachmentsTitle, _attachmentList);
        attachmentsColumn.EdgeInsets = new NSEdgeInsets(10, 12, 10, 12);
        _attachmentList.WidthAnchor.ConstraintEqualTo(attachmentsColumn.WidthAnchor, 1, -24).Active = true;
        _attachmentsCard = new WinoSurfaceView { Fill = CardFill, CornerRadius = 4, Hidden = true };
        WinoLayout.Fill(attachmentsColumn, _attachmentsCard);
        AddRow(_attachmentsCard);

        _column.DetachesHiddenViews = true;
        var document = new FlippedView();
        WinoLayout.Fill(_column, document);
        var scroll = new NSScrollView { DocumentView = document, DrawsBackground = false, HasVerticalScroller = true, AutohidesScrollers = true, BorderType = NSBorderType.NoBorder, TranslatesAutoresizingMaskIntoConstraints = false };
        document.WidthAnchor.ConstraintEqualTo(scroll.ContentView.WidthAnchor).Active = true;
        var root = new AppearanceView { AppearanceChanged = () => { if (ViewModel.CurrentEvent is { } current) ApplyDescription(current.CalendarItem.Description); } };
        WinoLayout.Fill(scroll, root);
        View = root;
    }

    private void AddRow(NSView view)
    {
        _column.AddArrangedSubview(view);
        view.WidthAnchor.ConstraintEqualTo(_column.WidthAnchor, 1, -32).Active = true;
    }

    /// <summary>Whether the shown event is on a read-only calendar (debug bridge and tests).</summary>
    internal bool IsReadOnlyEvent => _isReadOnly;

    /// <summary>A one-line summary of the read-only presentation, for the debug bridge.</summary>
    internal string DescribeReadOnlyState()
        => $"title='{ViewModel.CurrentEvent?.Title}' readOnly={_isReadOnly} bar={!_readOnlyBar.Hidden} delete={!_delete.Hidden} rsvp={!_rsvp.Hidden} showAs={_showAs.Enabled}";

    private ActionBarButton Action(WinoIconGlyph glyph, string title, System.Windows.Input.ICommand command)
    {
        var button = new ActionBarButton(glyph, title, title);
        var binding = Bindings.Own(new CommandBinding(command, () => null, enabled => button.Enabled = enabled, Dispatcher, ReportError));
        button.Activated += (_, _) => binding.Execute();
        return button;
    }

    private ActionBarButton RsvpButton(WinoIconGlyph glyph, NSColor tint, string title, AttendeeStatus status)
    {
        var button = new ActionBarButton(glyph, title, title, tint);
        var binding = Bindings.Own(new CommandBinding(ViewModel.SendRsvpResponseCommand, () => status, enabled => button.Enabled = enabled, Dispatcher, ReportError));
        button.Activated += (_, _) => binding.Execute();
        return button;
    }

    protected override Task InitializeAsync(NavigationMode mode, object? parameter)
    {
        ViewModel.OnNavigatedTo(mode, parameter!);
        Bind(nameof(ViewModel.CurrentEvent), vm => vm.CurrentEvent, _ => ApplyEvent());
        Bind(nameof(ViewModel.IsRsvpPanelVisible), vm => vm.IsRsvpPanelVisible, visible => _rsvpPanel.Hidden = !visible || _isReadOnly);
        Bind(nameof(ViewModel.RsvpMessage), vm => vm.RsvpMessage, value => { if (_rsvpMessage.StringValue != value) _rsvpMessage.StringValue = value ?? string.Empty; });
        Bind(nameof(ViewModel.SelectedShowAsOption), vm => vm.SelectedShowAsOption, option => { if (option is not null) _showAs.SelectItem(option.DisplayText); });
        Bind(nameof(ViewModel.CurrentRsvpText), vm => vm.CurrentRsvpText, _ => ApplyRsvp());
        Bind(nameof(ViewModel.CanEditSeries), vm => vm.CanEditSeries, can => _series.Hidden = !can);
        Bind(nameof(ViewModel.Reminders), vm => vm.Reminders, _ => UpdateReminderRow());
        Bind(nameof(ViewModel.HasAttachments), vm => vm.HasAttachments, _ => ApplyAttachments());
        return Task.CompletedTask;
    }

    private void Bind<TValue>(string property, Func<EventDetailsPageViewModel, TValue> read, Action<TValue> apply)
        => Bindings.Own(new PropertyBinding<EventDetailsPageViewModel, TValue>(ViewModel, property, read, apply, Dispatcher, ReportError));

    private void ApplyEvent()
    {
        var item = ViewModel.CurrentEvent;
        _eventBindings?.Dispose();
        _eventBindings = null;
        if (item is null) return;
        _eventBindings = new BindingScope();
        _eventBindings.Own(new PropertyBinding<CalendarItemViewModel, string>(item, nameof(item.Title), i => i.Title, _ => ApplyEventFields(item), Dispatcher, ReportError));
        _eventBindings.Own(new PropertyBinding<CalendarItemViewModel, bool>(item, nameof(item.IsBusy), i => i.IsBusy, busy => View.AlphaValue = busy ? (nfloat)0.7 : 1, Dispatcher, ReportError));
        item.Attendees.CollectionChanged += AttendeesChanged;
        _eventBindings.Own(new ActionDisposable(() => item.Attendees.CollectionChanged -= AttendeesChanged));
        ApplyEventFields(item);
        ApplyAttendees();
        ApplyRsvp();
    }

    private void AttendeesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs args)
        => _ = Dispatcher.ExecuteOnUIThread(ApplyAttendees);

    private void ApplyEventFields(CalendarItemViewModel item)
    {
        var settings = ViewModel.CurrentSettings;
        _title.StringValue = item.Title ?? string.Empty;
        _colorSwatch.Fill = CalendarTileMapper.Fill(item);
        _when.Text = CalendarTileMapper.DateString(item, settings);
        _location.Text = item.Location;
        _online.Text = CalendarJoinLinkResolver.TryGetEffectiveJoinUri(item.CalendarItem, out var uri) ? uri.Host : null;
        _repeat.Text = item.IsRecurringEvent ? CalendarTileMapper.RecurrenceString(item, settings) : null;
        var calendar = item.AssignedCalendar;
        _calendar.Text = calendar is null ? null : string.IsNullOrEmpty(calendar.MailAccount?.Name) ? calendar.Name : $"{calendar.Name} · {calendar.MailAccount.Name}";
        UpdateReminderRow();
        ApplyDescription(item.CalendarItem.Description);
        ApplyReadOnly(item);
    }

    /// <summary>Shows the read-only notice and hides Delete and RSVP, and locks Show as, for read-only calendars.</summary>
    private void ApplyReadOnly(CalendarItemViewModel item)
    {
        _isReadOnly = CalendarTileMapper.IsReadOnly(item);
        _readOnlyBar.Hidden = !_isReadOnly;
        _delete.Hidden = _isReadOnly;
        _rsvp.Hidden = _isReadOnly;
        _showAs.Enabled = !_isReadOnly;
        if (_isReadOnly) _rsvpPanel.Hidden = true;
        else _rsvpPanel.Hidden = !ViewModel.IsRsvpPanelVisible;
    }

    private void UpdateReminderRow()
    {
        var selected = ViewModel.ReminderOptions.Where(option => option.IsSelected).Select(option => option.DisplayText).ToList();
        _reminderRow.Text = selected.Count == 0 ? null : string.Join(", ", selected);
    }

    private void ApplyRsvp()
    {
        var (glyph, tint) = CalendarTileMapper.RsvpGlyph(ViewModel.CurrentRsvpStatus);
        _rsvp.SetGlyph(glyph, tint);
        _rsvp.Title = ViewModel.CurrentRsvpText;
    }

    private void ApplyAttendees()
    {
        foreach (var view in _peopleList.ArrangedSubviews.ToArray()) { _peopleList.RemoveArrangedSubview(view); view.RemoveFromSuperview(); }
        var attendees = ViewModel.CurrentEvent?.Attendees;
        bool any = attendees is { Count: > 0 };
        _peopleCard.Hidden = !any;
        if (!any) return;
        _peopleTitle.StringValue = $"{Translator.CalendarEventDetails_People} · {attendees!.Count}";
        foreach (var attendee in attendees)
        {
            var row = AttendeeRow(attendee);
            _peopleList.AddArrangedSubview(row);
            row.WidthAnchor.ConstraintEqualTo(_peopleList.WidthAnchor).Active = true;
        }
    }

    private static NSView AttendeeRow(CalendarEventAttendee attendee)
    {
        var picture = new WinoContactPicture(24);
        picture.SetIdentity(string.IsNullOrWhiteSpace(attendee.Name) ? attendee.Email : attendee.Name, attendee.Email);
        var name = WinoStyle.Label(string.IsNullOrWhiteSpace(attendee.Name) ? attendee.Email : attendee.Name, WinoStyle.Body);
        name.ToolTip = attendee.Email;
        var children = new List<NSView> { picture, name };
        if (attendee.IsOrganizer)
        {
            var badge = new WinoSurfaceView { Fill = WinoStyle.Accent, CornerRadius = 4 };
            var badgeText = WinoStyle.Label(Translator.CalendarEventDetails_Organizer, WinoStyle.CaptionStrong, NSColor.White);
            WinoLayout.Fill(badgeText, badge, 1, 6, 1, 6);
            children.Add(badge);
        }
        children.Add(WinoLayout.Spacer());
        var (glyph, tint) = CalendarTileMapper.AttendeeStatusGlyph(attendee.AttendenceStatus);
        var status = new WinoIconView(glyph, 14, tint) { ToolTip = CalendarTileMapper.AttendeeStatusText(attendee.AttendenceStatus) };
        children.Add(status);
        var row = WinoLayout.HStack(8, children.ToArray());
        WinoLayout.Size(row, -1, 30);
        return row;
    }

    private void ApplyDescription(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            _descriptionScroll.Hidden = true;
            return;
        }
        NSAttributedString? attributed = null;
        try
        {
            string document = html.Contains('<') ? html : $"<p>{System.Net.WebUtility.HtmlEncode(html).Replace("\n", "<br>")}</p>";
            var styled = $"<meta charset=\"utf-8\"><style>body{{font-family:-apple-system;font-size:12px;color:{(WinoIcons.IsDark(View.EffectiveAppearance) ? "#e6e6e6" : "#1b1b1b")};}}</style>{document}";
            attributed = NSAttributedString.CreateWithHTML(NSData.FromString(styled, NSStringEncoding.UTF8), out NSDictionary _);
        }
        catch (Exception exception) { ReportError(exception); }
        attributed ??= new NSAttributedString(html, new NSStringAttributes { Font = NSFont.SystemFontOfSize(12), ForegroundColor = WinoStyle.PrimaryText });
        attributed = TrimEdges(attributed);
        // Drop baked-in foreground colours so the text follows light/dark through the view's TextColor; links keep their own style.
        var mutable = new NSMutableAttributedString(attributed);
        var whole = new NSRange(0, (nint)mutable.Length);
        mutable.RemoveAttribute(NSStringAttributeKey.ForegroundColor, whole);
        mutable.AddAttribute(NSStringAttributeKey.ForegroundColor, WinoStyle.PrimaryText, whole);
        _description.TextStorage?.SetString(mutable);
        _descriptionScroll.Hidden = false;
        _description.LayoutManager?.EnsureLayoutForTextContainer(_description.TextContainer!);
        var height = Math.Min(260, Math.Max(80, _description.LayoutManager?.GetUsedRect(_description.TextContainer!).Height ?? 80) + 8);
        _descriptionHeight ??= _descriptionScroll.HeightAnchor.ConstraintEqualTo((nfloat)height);
        _descriptionHeight.Constant = (nfloat)height;
        _descriptionHeight.Active = true;
    }

    private NSLayoutConstraint? _descriptionHeight;

    /// <summary>HTML import leaves blank paragraphs at both ends; drop them.</summary>
    private static NSAttributedString TrimEdges(NSAttributedString text)
    {
        var value = text.Value ?? string.Empty;
        int start = 0, end = value.Length;
        while (start < end && char.IsWhiteSpace(value[start])) start++;
        while (end > start && char.IsWhiteSpace(value[end - 1])) end--;
        return start == 0 && end == value.Length ? text : text.Substring(start, end - start);
    }

    private void ApplyAttachments()
    {
        foreach (var view in _attachmentList.ArrangedSubviews.ToArray()) { _attachmentList.RemoveArrangedSubview(view); view.RemoveFromSuperview(); }
        _attachmentsCard.Hidden = !ViewModel.HasAttachments;
        if (!ViewModel.HasAttachments) return;
        foreach (var attachment in ViewModel.Attachments)
        {
            var icon = new WinoIconView(WinoIconGlyph.Attachment, 16, WinoStyle.SecondaryText);
            var name = WinoStyle.Label(attachment.FileName, WinoStyle.Body);
            var size = WinoStyle.Label(attachment.ReadableSize, WinoStyle.Caption, WinoStyle.TertiaryText);
            var open = new NSButton { Title = Translator.Buttons_Open, BezelStyle = NSBezelStyle.Inline, ControlSize = NSControlSize.Small, TranslatesAutoresizingMaskIntoConstraints = false };
            var captured = attachment;
            open.Activated += (_, _) => ViewModel.OpenAttachmentCommand.Execute(captured);
            var row = WinoLayout.HStack(8, icon, name, size, WinoLayout.Spacer(), open);
            WinoLayout.Size(row, -1, 30);
            _attachmentList.AddArrangedSubview(row);
            row.WidthAnchor.ConstraintEqualTo(_attachmentList.WidthAnchor).Active = true;
        }
    }

    protected override Task DeactivateAsync()
    {
        _eventBindings?.Dispose();
        _eventBindings = null;
        return base.DeactivateAsync();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _eventBindings?.Dispose(); _eventBindings = null; }
        base.Dispose(disposing);
    }

    private sealed class FlippedView : NSView
    {
        public FlippedView() { TranslatesAutoresizingMaskIntoConstraints = false; }
        public override bool IsFlipped => true;
    }

    /// <summary>Root view that re-renders the HTML description (whose colours are baked in) on light/dark switches.</summary>
    private sealed class AppearanceView : NSView
    {
        public Action? AppearanceChanged { get; init; }
        public override void ViewDidChangeEffectiveAppearance()
        {
            base.ViewDidChangeEffectiveAppearance();
            AppearanceChanged?.Invoke();
        }
    }
}

/// <summary>A glyph + label row (Clock, Location, Reminder, Calendar) that hides itself when empty.</summary>
internal sealed class DetailRow : NSStackView
{
    private readonly NSTextField _label;

    public DetailRow(WinoIconGlyph glyph)
    {
        Orientation = NSUserInterfaceLayoutOrientation.Horizontal;
        Spacing = 10;
        Alignment = NSLayoutAttribute.Top;
        TranslatesAutoresizingMaskIntoConstraints = false;
        var icon = new WinoIconView(glyph, 16, WinoStyle.SecondaryText);
        _label = WinoStyle.Label(string.Empty, NSFont.SystemFontOfSize(12), WinoStyle.SecondaryText, 3);
        AddArrangedSubview(icon);
        AddArrangedSubview(_label);
        Hidden = true;
    }

    public string? Text
    {
        get => _label.StringValue;
        set { _label.StringValue = value ?? string.Empty; Hidden = string.IsNullOrWhiteSpace(value); }
    }
}

/// <summary>Windows TransparentActionButtonStyle: glyph, label, hover fill, 28pt tall.</summary>
internal sealed class ActionBarButton : WinoSurfaceView
{
    private static readonly NSColor HoverFill = WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.06), WinoStyle.Hex(0xFFFFFF, 0.10));
    private readonly WinoIconView _icon;
    private readonly NSTextField? _label;
    private bool _enabled = true;

    public ActionBarButton(WinoIconGlyph glyph, string? title, string accessibilityLabel, NSColor? tint = null)
    {
        CornerRadius = 5;
        _icon = new WinoIconView(glyph, 14, tint);
        var children = new List<NSView> { _icon };
        if (title is not null) { _label = WinoStyle.Label(title, NSFont.SystemFontOfSize(12)); children.Add(_label); }
        var stack = WinoLayout.HStack(6, children.ToArray());
        stack.EdgeInsets = new NSEdgeInsets(0, 8, 0, 8);
        WinoLayout.Fill(stack, this);
        HeightAnchor.ConstraintEqualTo(28).Active = true;
        AccessibilityElement = true;
        AccessibilityRole = NSAccessibilityRoles.ButtonRole;
        AccessibilityLabel = accessibilityLabel;
        ToolTip = accessibilityLabel;
    }

    public event EventHandler? Activated;

    public string Title
    {
        set { if (_label is not null) _label.StringValue = value ?? string.Empty; }
    }

    public bool Enabled
    {
        get => _enabled;
        set { _enabled = value; AlphaValue = value ? 1 : (nfloat)0.4; }
    }

    public void SetGlyph(WinoIconGlyph glyph, NSColor? tint)
    {
        _icon.Icon = glyph;
        _icon.Tint = tint;
    }

    public override void UpdateTrackingAreas()
    {
        base.UpdateTrackingAreas();
        foreach (var area in TrackingAreas()) RemoveTrackingArea(area);
        AddTrackingArea(new NSTrackingArea(Bounds, NSTrackingAreaOptions.MouseEnteredAndExited | NSTrackingAreaOptions.ActiveInKeyWindow | NSTrackingAreaOptions.InVisibleRect, this, null));
    }

    public override void MouseEntered(NSEvent theEvent) { if (_enabled) Fill = HoverFill; }
    public override void MouseExited(NSEvent theEvent) => Fill = null;
    public override void MouseDown(NSEvent theEvent) { }
    public override void MouseUp(NSEvent theEvent)
    {
        if (_enabled && Bounds.Contains(ConvertPointFromView(theEvent.LocationInWindow, null))) Activated?.Invoke(this, EventArgs.Empty);
    }

    public override bool AccessibilityPerformPress()
    {
        if (_enabled) Activated?.Invoke(this, EventArgs.Empty);
        return _enabled;
    }
}
