using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Messaging.Client.Calendar;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.ViewModels.Calendar;
using Wino.Mail.ViewModels.Calendar.Data;
using Wino.Presentation.Bindings;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Bindings;

namespace Wino.Mail.Linux.Views.Calendar;

/// <summary>
/// Event details (macOS EventDetailsPageViewController, Windows EventDetailsPage) in the calendar page's details zone or
/// in a window of its own: the read-only notice, the action card (Join online, response, Edit, Edit series, Delete,
/// Open in new window; Show as and Reminder menus that save at once), the RSVP panel, the title with the calendar colour,
/// detail rows, people with their response, the notes and the attachments.
/// </summary>
internal sealed partial class EventDetailsView : WinoView<EventDetailsPageViewModel>
{
    private readonly IServiceProvider _services;
    private readonly IPlatformCapabilities _capabilities;
    private readonly Gtk.Box _root = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
    private readonly Adw.Banner _readOnly = Adw.Banner.New($"{Translator.CalendarReadOnly_Title}. {Translator.CalendarReadOnly_Message}");
    private readonly Gtk.Box _column = Gtk.Box.New(Gtk.Orientation.Vertical, 12);
    private readonly Gtk.Button _join;
    private readonly Gtk.Button _rsvp = Gtk.Button.New();
    private readonly Gtk.Label _rsvpGlyph = WinoIcons.Label(WinoIconGlyph.EventRespond, 16);
    private readonly Gtk.Label _rsvpText = Gtk.Label.New(string.Empty);
    private readonly Gtk.Button _edit;
    private readonly Gtk.Button _editSeries;
    private readonly Gtk.Button _delete;
    private readonly Gtk.Button _followUp;
    private readonly Gtk.Button _openInWindow;
    private readonly Gtk.Button _showAs = Gtk.Button.New();
    private readonly Gtk.Label _showAsText = Gtk.Label.New(string.Empty);
    private readonly Gtk.Button _reminder = Gtk.Button.New();
    private readonly Gtk.Label _reminderText = Gtk.Label.New(string.Empty);
    private readonly Gtk.Box _rsvpPanel = Gtk.Box.New(Gtk.Orientation.Vertical, 8);
    private readonly Gtk.Entry _rsvpMessage = Gtk.Entry.New();
    private readonly Gtk.Label _responseNotSent = Gtk.Label.New(string.Empty);
    private readonly CalendarColorDot _dot = new();
    private readonly Gtk.Label _title = Gtk.Label.New(string.Empty);
    private WinoMenuPopover? _openMenu;
    private GtkBinder<EventDetailsPageViewModel>? _eventScope;
    private CalendarItemShowAs? _persistedShowAs;
    private bool _isReadOnly;
    private bool _saving;
    private bool _savePending;
    private Task _saveLoop = Task.CompletedTask;

    public EventDetailsView(EventDetailsPageViewModel viewModel, WinoViewServices services, IServiceProvider provider, IPlatformCapabilities capabilities)
        : base(viewModel, services)
    {
        _services = provider;
        _capabilities = capabilities;

        // An unrevealed banner still reports its title's natural width, which would widen the floating pane.
        _readOnly.SetRevealed(false);
        _readOnly.SetVisible(false);
        _root.Append(_readOnly);

        _column.SetMarginTop(16);
        _column.SetMarginBottom(16);
        _column.SetMarginStart(16);
        _column.SetMarginEnd(16);

        _join = ActionButton(WinoIconGlyph.EventJoinOnline, Translator.CalendarEventDetails_JoinOnline);
        var rsvpContent = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
        rsvpContent.Append(_rsvpGlyph);
        rsvpContent.Append(_rsvpText);
        _rsvp.SetChild(rsvpContent);
        _rsvp.AddCssClass("flat");
        _edit = ActionButton(WinoIconGlyph.Edit, Translator.CalendarEventDetails_Edit);
        _editSeries = ActionButton(WinoIconGlyph.EventEditSeries, Translator.CalendarEventDetails_EditSeries);
        _delete = ActionButton(WinoIconGlyph.Delete, Translator.Buttons_Delete);
        _followUp = ActionButton(WinoIconGlyph.TaskList, Translator.CalendarContextMenu_CreateTask);
        _openInWindow = WinoIcons.Button(WinoIconGlyph.OpenInNewWindow, Translator.CalendarEventDetails_OpenInNewWindow);
        var actions = Adw.WrapBox.New();
        actions.SetChildSpacing(2);
        actions.SetLineSpacing(2);
        foreach (var button in (Gtk.Widget[])[_join, _rsvp, _edit, _editSeries, _delete, _followUp]) actions.Append(button);

        MenuButton(_showAs, _showAsText, WinoIconGlyph.CalendarShowAs, Translator.CalendarEventDetails_ShowAs);
        MenuButton(_reminder, _reminderText, WinoIconGlyph.Reminder, Translator.CalendarEventDetails_Reminder);
        var menus = Gtk.Box.New(Gtk.Orientation.Horizontal, 10);
        menus.Append(_showAs);
        menus.Append(_reminder);
        var menusEnd = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
        menusEnd.SetHexpand(true);
        menus.Append(menusEnd);
        menus.Append(_openInWindow);

        var actionCard = Gtk.Box.New(Gtk.Orientation.Vertical, 4);
        actionCard.AddCssClass("card");
        actionCard.SetMarginTop(0);
        foreach (var part in (Gtk.Widget[])[actions, menus])
        {
            part.SetMarginStart(6);
            part.SetMarginEnd(6);
        }
        actions.SetMarginTop(6);
        menus.SetMarginBottom(6);
        actionCard.Append(actions);
        actionCard.Append(menus);
        _column.Append(actionCard);

        BuildRsvpPanel();
        _column.Append(_rsvpPanel);

        _title.SetWrap(true);
        _title.SetLines(3);
        _title.SetEllipsize(Pango.EllipsizeMode.End);
        _title.SetXalign(0);
        _title.SetSelectable(true);
        _title.SetHexpand(true);
        _title.AddCssClass("title-4");
        _dot.Root.SetValign(Gtk.Align.Start);
        _dot.Root.SetMarginTop(7);
        var titleRow = Gtk.Box.New(Gtk.Orientation.Horizontal, 10);
        titleRow.Append(_dot.Root);
        titleRow.Append(_title);
        _column.Append(titleRow);

        BuildSections();

        var scroller = Gtk.ScrolledWindow.New();
        scroller.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        scroller.SetVexpand(true);
        scroller.SetChild(_column);
        _root.Append(scroller);
        WinoA11y.Label(_root, Translator.CalendarEventDetails_Details);
    }

    public override Gtk.Widget Root => _root;

    /// <summary>Set before activation by <see cref="EventDetailsWindow"/>; the pane leaves it false.</summary>
    internal bool IsInOwnWindow { get; set; }

    internal bool IsReadOnlyEvent => _isReadOnly;

    internal bool CanFollowUp => _followUp.GetVisible();

#if DEBUG
    internal string DescribeReminders()
        => $"button='{_reminderText.GetText()}' tooltip='{_reminder.GetTooltipText()}' row='{_reminderRow.Text}' stored=[{string.Join(",", (ViewModel.Reminders ?? []).Select(static r => $"{r.DurationInSeconds}:{r.ReminderType}"))}]\n" +
           string.Join('\n', ViewModel.ReminderOptions.Select(static o => $"{o.DisplayText}|{o.IsSelected}|custom={o.IsCustom}"));
#endif

    internal Gtk.Window? OwnWindow { get; set; }

    internal IServiceProvider Services => _services;

    protected override async Task InitializeAsync(NavigationMode mode, object? parameter)
    {
        // The pane is an inspector: a Show as or reminder choice saves at once and the pane stays; a window never navigates the shell.
        ViewModel.NavigatesBackAfterSave = false;
        ViewModel.CanNavigateShell = !IsInOwnWindow;
        _openInWindow.SetVisible(!IsInOwnWindow && EventDetailsWindow.IsAvailable(_capabilities));

        Bindings.Own(GtkSignals.OnClicked(_join, () => ViewModel.JoinOnlineCommand.Execute(null)));
        Bind.BindCommand(_rsvp, ViewModel.ToggleRsvpPanelCommand);
        Bind.BindCommand(_edit, ViewModel.EditEventCommand);
        Bind.BindCommand(_editSeries, ViewModel.EditEventSeriesCommand);
        Bind.BindCommand(_delete, ViewModel.DeleteCommand);
        Bind.BindCommand(_followUp, ViewModel.CreateFollowUpTaskCommand);
        _followUp.SetVisible(ViewModel.CanCreateFollowUpTask);
        Bindings.Own(GtkSignals.OnClicked(_openInWindow, OpenInNewWindow));
        Bindings.Own(GtkSignals.OnClicked(_showAs, ShowShowAsMenu));
        Bindings.Own(GtkSignals.OnClicked(_reminder, ShowReminderMenu));

        Bind.Bind(nameof(EventDetailsPageViewModel.CurrentEvent), static vm => vm.CurrentEvent, _ => ApplyEvent());
        Bind.Bind(nameof(EventDetailsPageViewModel.CurrentSettings), static vm => vm.CurrentSettings, _ =>
        {
            if (ViewModel.CurrentEvent is { } item) ApplyEventFields(item);
        });
        Bind.Bind(nameof(EventDetailsPageViewModel.IsRsvpPanelVisible), static vm => vm.IsRsvpPanelVisible, _ => ApplyRsvpPanel());
        Bind.BindText(_rsvpMessage, nameof(EventDetailsPageViewModel.RsvpMessage), static vm => vm.RsvpMessage, static (vm, text) => vm.RsvpMessage = text);
        Bind.Bind(nameof(EventDetailsPageViewModel.SelectedShowAsOption), static vm => vm.SelectedShowAsOption, option => _showAsText.SetText(option?.DisplayText ?? Translator.CalendarEventDetails_ShowAs));
        Bind.Bind(nameof(EventDetailsPageViewModel.CurrentRsvpText), static vm => vm.CurrentRsvpText, _ => ApplyRsvp());
        Bind.Bind(nameof(EventDetailsPageViewModel.CanEditEvent), static vm => vm.CanEditEvent, _ => ApplyEditButtons());
        Bind.Bind(nameof(EventDetailsPageViewModel.EditEventText), static vm => vm.EditEventText, _ => ApplyEditButtons());
        Bind.Bind(nameof(EventDetailsPageViewModel.CanEditEventSeries), static vm => vm.CanEditEventSeries, _ => ApplyEditButtons());
        Bind.Bind(nameof(EventDetailsPageViewModel.Reminders), static vm => vm.Reminders, _ => UpdateReminderRow());
        Bind.Bind(nameof(EventDetailsPageViewModel.HasResponseNotSentWarning), static vm => vm.HasResponseNotSentWarning, _ => ApplyRsvpPanel());
        Bind.BindCollection(ViewModel.ReminderOptions, UpdateReminderRow);
        Bind.BindCollection(ViewModel.Attachments, ApplyAttachments);
        Subscribe<CalendarDescriptionRenderingRequested>(_ =>
        {
            if (ViewModel.CurrentEvent is { } item) ApplyNotes(item.CalendarItem.Description);
        });
        InitializeNotes();
        InitializeLinks();

        await ViewModel.InitializeNavigationAsync(mode, parameter);
    }

    protected override async Task DeactivateAsync()
    {
        _openMenu?.Popdown();
        _openMenu = null;
        _eventScope?.Scope.Dispose();
        _eventScope = null;
        await DisposeNotesAsync();
        await base.DeactivateAsync();
    }

    private Gtk.Button ActionButton(WinoIconGlyph glyph, string text)
    {
        var button = Gtk.Button.New();
        button.SetChild(WinoIcons.ButtonContent(glyph, text, 16));
        button.AddCssClass("flat");
        WinoA11y.Rename(button, text);
        return button;
    }

    private static void MenuButton(Gtk.Button button, Gtk.Label text, WinoIconGlyph glyph, string name)
    {
        var content = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
        var icon = WinoIcons.Label(glyph, 16);
        icon.AddCssClass("dim-label");
        text.SetEllipsize(Pango.EllipsizeMode.End);
        text.SetMaxWidthChars(14);
        var arrow = Gtk.Image.NewFromIconName("pan-down-symbolic");
        content.Append(icon);
        content.Append(text);
        content.Append(arrow);
        button.SetChild(content);
        button.AddCssClass("flat");
        button.SetTooltipText(name);
        WinoA11y.Rename(button, name);
        WinoA11y.HasPopup(button);
    }

    private void BuildRsvpPanel()
    {
        _rsvpPanel.AddCssClass("card");
        var buttons = Gtk.Box.New(Gtk.Orientation.Horizontal, 2);
        buttons.SetMarginTop(8);
        buttons.SetMarginStart(8);
        buttons.SetMarginEnd(8);
        buttons.Append(RsvpButton(WinoIconGlyph.EventAccept, "success", Translator.CalendarEventRsvpPanel_Accept, AttendeeStatus.Accepted));
        buttons.Append(RsvpButton(WinoIconGlyph.EventTentative, "warning", Translator.CalendarEventRsvpPanel_Tentative, AttendeeStatus.Tentative));
        buttons.Append(RsvpButton(WinoIconGlyph.EventDecline, "error", Translator.CalendarEventRsvpPanel_Decline, AttendeeStatus.Declined));
        var spacer = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
        spacer.SetHexpand(true);
        buttons.Append(spacer);
        var close = WinoIcons.Button(WinoIconGlyph.Dismiss, Translator.Buttons_Close);
        Bindings.Own(GtkSignals.OnClicked(close, () => ViewModel.CloseRsvpPanelCommand.Execute(null)));
        buttons.Append(close);
        _rsvpMessage.SetPlaceholderText(Translator.CalendarEventRsvpPanel_AddMessage);
        WinoA11y.Label(_rsvpMessage, Translator.CalendarEventRsvpPanel_AddMessage);
        _rsvpMessage.SetMarginStart(8);
        _rsvpMessage.SetMarginEnd(8);
        _responseNotSent.SetWrap(true);
        _responseNotSent.SetXalign(0);
        _responseNotSent.AddCssClass("caption");
        _responseNotSent.AddCssClass("dim-label");
        _responseNotSent.SetMarginStart(8);
        _responseNotSent.SetMarginEnd(8);
        _responseNotSent.SetMarginBottom(8);
        _rsvpPanel.Append(buttons);
        _rsvpPanel.Append(_rsvpMessage);
        _rsvpPanel.Append(_responseNotSent);
        _rsvpPanel.SetVisible(false);
    }

    private Gtk.Button RsvpButton(WinoIconGlyph glyph, string tone, string text, AttendeeStatus status)
    {
        var content = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
        var icon = WinoIcons.Label(glyph, 16);
        icon.AddCssClass(tone);
        content.Append(icon);
        content.Append(Gtk.Label.New(text));
        var button = Gtk.Button.New();
        button.SetChild(content);
        button.AddCssClass("flat");
        WinoA11y.Rename(button, text);
        Bind.BindCommand(button, ViewModel.SendRsvpResponseCommand, () => status);
        return button;
    }

    private void ApplyEvent()
    {
        _eventScope?.Scope.Dispose();
        _eventScope = null;
        var item = ViewModel.CurrentEvent;
        if (item is null) return;
        if (!_saving) _persistedShowAs = item.CalendarItem.ShowAs;
        var scope = _eventScope = Bind.Child().For(ViewModel);
        var itemBinder = scope.For(item);
        itemBinder.Bind(nameof(CalendarItemViewModel.Title), static i => i.Title, _ => ApplyEventFields(item));
        itemBinder.Bind(nameof(CalendarItemViewModel.IsBusy), static i => i.IsBusy, busy => _column.SetOpacity(busy ? 0.7 : 1));
        scope.BindCollection(item.Attendees, ApplyAttendees);
        ApplyEventFields(item);
        ApplyRsvp();
    }

    private void ApplyRsvp()
    {
        var (glyph, tone) = RsvpGlyph(ViewModel.CurrentRsvpStatus);
        WinoIcons.SetGlyph(_rsvpGlyph, WinoIcons.Glyph(glyph));
        foreach (var css in (string[])["success", "warning", "error", "accent"]) _rsvpGlyph.RemoveCssClass(css);
        _rsvpGlyph.AddCssClass(tone);
        _rsvpText.SetText(ViewModel.CurrentRsvpText ?? string.Empty);
        WinoA11y.Rename(_rsvp, ViewModel.CurrentRsvpText ?? Translator.CalendarEventResponse_NotResponded);
    }

    private void ApplyRsvpPanel()
    {
        _rsvpPanel.SetVisible(ViewModel.IsRsvpPanelVisible && !_isReadOnly);
        _responseNotSent.SetText(ViewModel.ResponseNotSentText ?? string.Empty);
        _responseNotSent.SetVisible(ViewModel.HasResponseNotSentWarning);
    }

    private void ApplyEditButtons()
    {
        _edit.SetVisible(ViewModel.CanEditEvent && !_isReadOnly);
        _edit.SetChild(WinoIcons.ButtonContent(WinoIconGlyph.Edit, ViewModel.EditEventText ?? Translator.CalendarEventDetails_Edit, 16));
        WinoA11y.Rename(_edit, ViewModel.EditEventText ?? Translator.CalendarEventDetails_Edit);
        _editSeries.SetVisible(ViewModel.CanEditEventSeries && !_isReadOnly);
    }

    /// <summary>Read-only calendars keep the event viewable but drop the actions that would change it.</summary>
    private void ApplyReadOnly(CalendarItemViewModel item)
    {
        _isReadOnly = CalendarTileMapper.IsReadOnly(item) || ViewModel.IsCalendarReadOnly;
        _readOnly.SetVisible(_isReadOnly);
        _readOnly.SetRevealed(_isReadOnly);
        _delete.SetVisible(!_isReadOnly);
        _rsvp.SetVisible(!_isReadOnly);
        _showAs.SetSensitive(!_isReadOnly);
        _reminder.SetSensitive(!_isReadOnly);
        ApplyEditButtons();
        ApplyRsvpPanel();
    }

    /// <summary>Raised once the event is open in its own window; the calendar page closes this inline pane.</summary>
    internal event EventHandler? PoppedOut;

    private void OpenInNewWindow()
    {
        if (ViewModel.CurrentEvent?.CalendarItem is not { } item) return;
        Observe(OpenInNewWindowAsync(item));
    }

    private async Task OpenInNewWindowAsync(CalendarItem item)
    {
        await EventDetailsWindow.OpenAsync(_services, item);
        if (!IsReleased) PoppedOut?.Invoke(this, EventArgs.Empty);
    }

    // ------------------------------------------------------------ Show as and reminders: commit on change

    private void ShowShowAsMenu()
    {
        var menu = WinoMenus.Create();
        foreach (var option in ViewModel.ShowAsOptions)
        {
            var captured = option;
            menu.Radio("showas", option.DisplayText, ReferenceEquals(option, ViewModel.SelectedShowAsOption), () => Observe(ChooseShowAsAsync(captured.ShowAs)));
        }
        ShowMenu(_showAs, menu);
    }

    private void ShowReminderMenu()
    {
        var menu = WinoMenus.Create();
        foreach (var option in ViewModel.ReminderOptions)
        {
            var captured = option;
            menu.Check(option.DisplayText, option.IsSelected, _ => Observe(ToggleReminderAsync(captured)));
        }
        menu.Separator();
        menu.Item(Translator.CalendarReminder_Customize, () => Observe(CustomizeRemindersAsync()), !_isReadOnly);
        ShowMenu(_reminder, menu);
    }

    private void ShowMenu(Gtk.Button anchor, WinoMenuBuilder menu)
    {
        _openMenu?.Popdown();
        _openMenu = ContextMenu.Show(anchor, menu.Build(), anchor.GetWidth() / 2.0, anchor.GetHeight(), () => _openMenu = null);
    }

    /// <summary>The Show as menu's body; completes after the save.</summary>
    internal Task ChooseShowAsAsync(CalendarItemShowAs showAs)
    {
        var option = ViewModel.ShowAsOptions.FirstOrDefault(candidate => candidate.ShowAs == showAs);
        if (option is null || ReferenceEquals(option, ViewModel.SelectedShowAsOption)) return _saveLoop;
        ViewModel.SelectedShowAsOption = option;
        return RequestSave();
    }

    /// <summary>The reminder menu's body; completes after the save.</summary>
    internal Task ToggleReminderAsync(int minutes)
    {
        var option = ViewModel.ReminderOptions.FirstOrDefault(candidate => candidate.Minutes == minutes);
        return option is null ? _saveLoop : ToggleReminderAsync(option);
    }

    private Task ToggleReminderAsync(ReminderOption option)
    {
        option.IsSelected = !option.IsSelected;
        UpdateReminderRow();
        return RequestSave();
    }

    /// <summary>
    /// Customize reminders… opens the shared editor; the pane saves at once like its check items do, so an edited set is
    /// saved as soon as the editor closes with Save.
    /// </summary>
    internal async Task CustomizeRemindersAsync()
    {
        _openMenu?.Popdown();
        var before = ReminderOption.GetSelectedDrafts(ViewModel.ReminderOptions);
        await ViewModel.EditRemindersCommand.ExecuteAsync(null);
        if (IsReleased) return;
        await Dispatcher.ExecuteOnUIThread(UpdateReminderRow);
        var after = ReminderOption.GetSelectedDrafts(ViewModel.ReminderOptions);
        if (before.Count == after.Count && before.All(after.Contains)) return;
        await RequestSave();
    }

    /// <summary>Saves never overlap: a request during a save runs one more save afterwards (the shared command refuses concurrent runs).</summary>
    private Task RequestSave()
    {
        if (_isReadOnly || IsReleased || ViewModel.CurrentEvent is null) return _saveLoop;
        if (_saving)
        {
            _savePending = true;
            return _saveLoop;
        }
        return _saveLoop = SaveLoopAsync();
    }

    private async Task SaveLoopAsync()
    {
        _saving = true;
        try
        {
            do
            {
                _savePending = false;
                var persisted = _persistedShowAs;
                await ViewModel.SaveCommand.ExecuteAsync(null);
                var succeeded = ViewModel.LastMutationSucceeded;
                await Dispatcher.ExecuteOnUIThread(() =>
                {
                    if (IsReleased) return;
                    if (succeeded) _persistedShowAs = ViewModel.SelectedShowAsOption?.ShowAs ?? persisted;
                    else
                    {
                        RevertToPersisted(persisted);
                        _savePending = false;
                    }
                });
            }
            while (_savePending && !IsReleased);
        }
        finally { _saving = false; }
    }

    /// <summary>A failed save leaves the controls on the persisted values, not on the rejected choice.</summary>
    private void RevertToPersisted(CalendarItemShowAs? persistedShowAs)
    {
        if (persistedShowAs is { } showAs)
        {
            if (ViewModel.CurrentEvent?.CalendarItem is { } calendarItem) calendarItem.ShowAs = showAs;
            ViewModel.SelectedShowAsOption = ViewModel.ShowAsOptions.FirstOrDefault(option => option.ShowAs == showAs) ?? ViewModel.SelectedShowAsOption;
        }
        ReminderOption.Select(ViewModel.ReminderOptions,
            (ViewModel.Reminders ?? []).Select(static reminder => new CalendarReminderDraft(reminder.DurationInSeconds, reminder.ReminderType)), selectionChanged: null);
        UpdateReminderRow();
    }

    private void UpdateReminderRow()
    {
        var selected = ViewModel.ReminderOptions.Where(static option => option.IsSelected).Select(static option => option.DisplayText).ToArray();
        _reminderRow.Text = selected.Length == 0 ? null : string.Join(", ", selected);
        var summary = selected.Length == 0 ? Translator.CalendarEventDetails_Reminder : ReminderOption.Summarize(ViewModel.ReminderOptions);
        _reminderText.SetText(summary);
        _reminder.SetTooltipText(summary);
    }

    internal static (WinoIconGlyph Glyph, string Tone) RsvpGlyph(CalendarItemStatus status) => status switch
    {
        CalendarItemStatus.Accepted => (WinoIconGlyph.EventAccept, "success"),
        CalendarItemStatus.Cancelled => (WinoIconGlyph.EventDecline, "error"),
        CalendarItemStatus.Tentative => (WinoIconGlyph.EventTentative, "warning"),
        _ => (WinoIconGlyph.EventRespond, "accent")
    };

    internal static (WinoIconGlyph Glyph, string Tone) AttendeeStatusGlyph(AttendeeStatus status) => status switch
    {
        AttendeeStatus.Accepted => (WinoIconGlyph.EventAccept, "success"),
        AttendeeStatus.Declined => (WinoIconGlyph.EventDecline, "error"),
        AttendeeStatus.Tentative => (WinoIconGlyph.EventTentative, "warning"),
        _ => (WinoIconGlyph.EventRespond, "dim-label")
    };

#if DEBUG
    internal void DebugOpenInNewWindow() => OpenInNewWindow();

    internal string Describe()
        => $"title='{_title.GetText()}' readOnly={_isReadOnly} banner={_readOnly.GetRevealed()} join={_join.GetVisible()}/{_join.GetSensitive()} rsvp='{_rsvpText.GetText()}' edit={_edit.GetVisible()} editSeries={_editSeries.GetVisible()} delete={_delete.GetVisible()} window={_openInWindow.GetVisible()} " +
           $"showAs='{_showAsText.GetText()}' reminders='{_reminderRow.Text}' when='{_when.Text}' location='{_location.Text}' online='{_online.Text}' repeat='{_repeat.Text}' calendar='{_calendar.Text}' " +
           $"attendees={DescribeAttendees()} attachments={ViewModel.Attachments.Count} notes={_notesHost.GetVisible()} inWindow={IsInOwnWindow} rsvpPanel={_rsvpPanel.GetVisible()} notSent={_responseNotSent.GetVisible()}";

    internal bool DebugIsSaving => _saving || _savePending;
#endif
}
