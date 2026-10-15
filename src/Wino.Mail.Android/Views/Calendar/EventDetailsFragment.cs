using System.Collections.Specialized;
using Android.Content;
using Android.Graphics.Drawables;
using Android.Text;
using Android.Views;
using Android.Widget;
using Google.Android.Material.AppBar;
using Google.Android.Material.Button;
using Google.Android.Material.Chip;
using Microsoft.Extensions.DependencyInjection;
using Wino.Calendar.ViewModels;
using Wino.Calendar.ViewModels.Data;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Presentation;
using Wino.Mail.Android.Infrastructure;
using Wino.Mail.Controls.Android.Common;
using Wino.Mail.Controls.Android.Settings;
using Wino.Presentation.Android;
using Wino.Presentation.Bindings;

namespace Wino.Mail.Android.Views.Calendar;

/// <summary>
/// Event details (Windows EventDetailsPage; the iOS and Mac details pages; canvas board CalendarTablet details zone) over
/// the shared <see cref="EventDetailsPageViewModel"/>, parameter <see cref="CalendarItemTarget"/>. Compact widths show it
/// full screen with Back; medium and expanded widths in the shell's details pane with Close. The bar carries Edit series
/// and Delete; the column has the read-only notice, the colour bar with the title and date, Join online, the RSVP
/// choices, the detail rows, Show as and Reminder (saved through the shared Save command), people, notes and
/// attachments. Read-only calendars keep the event viewable and drop the actions that would change it.
/// </summary>
public sealed class EventDetailsFragment : WinoFragment<EventDetailsPageViewModel>
{
    private const int MenuEditSeries = 1;
    private const int MenuDelete = 2;

    private LinearLayout? _column;
    private MaterialToolbar? _toolbar;
    private BindingScope? _eventBindings;
    private bool _compact;

    protected override int LayoutResource => Resource.Layout.fragment_calendar_form;

    /// <summary>Route hook: saving keeps the page open; Delete still navigates the shell back to the calendar (iOS twin).</summary>
    public static Task InitializeAsync(EventDetailsPageViewModel viewModel, NavigationMode mode, object? parameter)
    {
        viewModel.NavigatesBackAfterSave = false;
        viewModel.CanNavigateShell = true;
        return viewModel.InitializeNavigationAsync(mode, parameter);
    }

    protected override void OnBind(View view, EventDetailsPageViewModel viewModel, IDispatcher dispatcher)
    {
        var services = MainApplication.Services ?? throw new InvalidOperationException("The application services are not ready.");
        var context = view.RequireContext();
        _compact = services.GetRequiredService<IWindowSizeClassService>().WidthClass == WindowWidthClass.Compact;
        _column = view.FindViewById<LinearLayout>(Resource.Id.calendar_form_column);
        _toolbar = view.FindViewById<MaterialToolbar>(Resource.Id.calendar_form_toolbar);
        if (view.FindViewById<WinoMaxWidthLayout>(Resource.Id.calendar_form_width) is { } width) width.MaxWidthDp = WinoTokens.Dp(context, Resource.Dimension.wino_calendar_editor_max_width);
        if (_compact) WinoInsets.Apply(view.FindViewById(Resource.Id.calendar_form_root) ?? view, top: true, bottom: true);

        if (_toolbar is not null)
        {
            _toolbar.Title = Translator.CalendarEventDetails_Details;
            _toolbar.SetNavigationIcon(_compact ? Resource.Drawable.wino_ic_arrow_back : Resource.Drawable.wino_ic_close);
            _toolbar.NavigationContentDescription = _compact ? Translator.Buttons_Back : Translator.Buttons_Close;
            _toolbar.NavigationClick += (_, _) => Close();
            _toolbar.MenuItemClick += (_, args) =>
            {
                switch (args.Item?.ItemId)
                {
                    case MenuEditSeries: Execute(viewModel.ViewSeriesCommand); break;
                    case MenuDelete: Execute(viewModel.DeleteCommand); break;
                }
            };
        }

        NotifyCollectionChangedEventHandler optionsChanged = (_, _) => view.Post(() => Rebuild(context, viewModel, dispatcher));
        viewModel.ReminderOptions.CollectionChanged += optionsChanged;
        viewModel.Attachments.CollectionChanged += optionsChanged;
        Bindings.Own(new ActionDisposable(() =>
        {
            viewModel.ReminderOptions.CollectionChanged -= optionsChanged;
            viewModel.Attachments.CollectionChanged -= optionsChanged;
            _eventBindings?.Dispose();
            _eventBindings = null;
        }));
        Bind(viewModel, dispatcher, nameof(viewModel.CurrentEvent), vm => vm.CurrentEvent, _ => ObserveEvent(context, viewModel, dispatcher));
        Bind(viewModel, dispatcher, nameof(viewModel.SelectedShowAsOption), vm => vm.SelectedShowAsOption, _ => Rebuild(context, viewModel, dispatcher));
        Bind(viewModel, dispatcher, nameof(viewModel.CurrentRsvpText), vm => vm.CurrentRsvpText, _ => Rebuild(context, viewModel, dispatcher));
        Bind(viewModel, dispatcher, nameof(viewModel.CanEditSeries), vm => vm.CanEditSeries, _ => ApplyMenu(context, viewModel));
    }

    private void ObserveEvent(Context context, EventDetailsPageViewModel viewModel, IDispatcher dispatcher)
    {
        _eventBindings?.Dispose();
        _eventBindings = null;
        if (viewModel.CurrentEvent is { } item)
        {
            var scope = _eventBindings = new BindingScope();
            scope.Own(new PropertyBinding<CalendarItemViewModel, string>(item, nameof(item.Title), i => i.Title, _ => Rebuild(context, viewModel, dispatcher), dispatcher, ReportError));
            NotifyCollectionChangedEventHandler attendees = (_, _) => View?.Post(() => Rebuild(context, viewModel, dispatcher));
            item.Attendees.CollectionChanged += attendees;
            scope.Own(new ActionDisposable(() => item.Attendees.CollectionChanged -= attendees));
        }
        ApplyMenu(context, viewModel);
        Rebuild(context, viewModel, dispatcher);
    }

    private void ApplyMenu(Context context, EventDetailsPageViewModel viewModel)
    {
        if (_toolbar is null) return;
        var menu = _toolbar.Menu;
        menu.Clear();
        if (viewModel.CurrentEvent is not { } item) return;
        float glyph = (float)WinoTokens.Dp(context, Resource.Dimension.wino_m3_glyph);
        var tint = WinoColors.SecondaryText(context);
        if (viewModel.CanEditSeries)
            menu.Add(0, MenuEditSeries, 0, Translator.CalendarEventDetails_EditSeries)?.SetIcon(WinoIcons.Drawable(context, WinoIconGlyph.EventEditSeries, glyph, tint))?.SetShowAsAction(ShowAsAction.Always);
        if (!CalendarEventDisplay.IsReadOnly(item))
            menu.Add(0, MenuDelete, 1, Translator.Buttons_Delete)?.SetIcon(WinoIcons.Drawable(context, WinoIconGlyph.Delete, glyph, tint))?.SetShowAsAction(ShowAsAction.Always);
    }

    /// <summary>Builds the column for the current event (it changes rarely: load, RSVP, Show as).</summary>
    private void Rebuild(Context context, EventDetailsPageViewModel viewModel, IDispatcher dispatcher)
    {
        if (_column is null) return;
        _column.RemoveAllViews();
        if (viewModel.CurrentEvent is not { } item) return;
        var settings = viewModel.CurrentSettings;
        bool readOnly = CalendarEventDisplay.IsReadOnly(item);
        int padding = WinoTokens.Px(context, Resource.Dimension.wino_space_l);

        if (readOnly)
        {
            var bar = new WinoInfoBar(context) { Severity = WinoInfoBarSeverity.Warning, Title = Translator.CalendarReadOnly_Title, Message = Translator.CalendarReadOnly_Message, IsClosable = false };
            _column.AddView(bar, Margins(context, padding, padding));
        }

        // Title with the calendar colour bar and the date line.
        var header = new LinearLayout(context) { Orientation = Orientation.Horizontal };
        header.SetPadding(padding, padding, padding, padding / 2);
        var colorBar = new View(context);
        var barFill = new GradientDrawable();
        barFill.SetColor(CalendarTiles.Fill(context, item));
        barFill.SetCornerRadius(WinoTokens.PxF(context, Resource.Dimension.wino_radius_small));
        colorBar.Background = barFill;
        header.AddView(colorBar, new LinearLayout.LayoutParams(WinoTokens.Px(context, Resource.Dimension.wino_calendar_details_bar_width), ViewGroup.LayoutParams.MatchParent) { MarginEnd = WinoTokens.Px(context, Resource.Dimension.wino_space_m) });
        var titles = new LinearLayout(context) { Orientation = Orientation.Vertical };
        var title = new TextView(context) { Text = item.Title ?? string.Empty };
        title.SetTextAppearance(Resource.Style.TextAppearance_WinoKit_HeadlineSmall);
        AndroidX.Core.View.ViewCompat.SetAccessibilityHeading(title, true);
        titles.AddView(title);
        titles.AddView(WinoTokens.Style(new TextView(context) { Text = CalendarEventDisplay.FormatDateRange(item.Period.Start, item.Period.End, item.IsAllDayEvent, item.IsMultiDayEvent, settings) }, WinoTextStyle.BodySecondary));
        header.AddView(titles, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1));
        _column.AddView(header);

        // Join online (only when the event carries a join link).
        if (CalendarJoinLinkResolver.TryGetEffectiveJoinUri(item.CalendarItem, out _))
        {
            var join = new MaterialButton(context) { Text = Translator.CalendarEventDetails_JoinOnline };
            join.Icon = WinoIcons.Drawable(context, WinoIconGlyph.EventJoinOnline, WinoTokens.Dp(context, Resource.Dimension.wino_glyph_m), WinoColors.OnAccent(context));
            WinoMaterialAccent.Tint(join, filled: true);
            join.Click += (_, _) => Execute(viewModel.JoinOnlineCommand);
            _column.AddView(join, Margins(context, padding, padding));
        }

        // RSVP (Windows response buttons): accept, tentative, decline; the current answer is checked.
        if (!readOnly)
        {
            var rsvp = new MaterialButtonToggleGroup(context) { SingleSelection = true };
            foreach (var (status, label, response) in new[]
            {
                (AttendeeStatus.Accepted, Translator.CalendarEventRsvpPanel_Accept, CalendarItemStatus.Accepted),
                (AttendeeStatus.Tentative, Translator.CalendarEventResponse_Tentative, CalendarItemStatus.Tentative),
                (AttendeeStatus.Declined, Translator.CalendarEventRsvpPanel_Decline, CalendarItemStatus.Cancelled)
            })
            {
                if (LayoutInflater.From(context)?.Inflate(Resource.Layout.mail_pivot_segment, rsvp, false) is not MaterialButton button) continue;
                button.Id = View.GenerateViewId();
                button.Text = label;
                bool current = viewModel.CurrentRsvpStatus == response;
                button.Icon = current ? AndroidX.Core.Content.ContextCompat.GetDrawable(context, Resource.Drawable.wino_ic_check) : null;
                WinoMaterialAccent.TintM3Segment(button);
                rsvp.AddView(button, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1));
                if (current) rsvp.Check(button.Id);
                var answer = status;
                button.Click += (_, _) => { if (viewModel.SendRsvpResponseCommand.CanExecute(answer)) viewModel.SendRsvpResponseCommand.Execute(answer); };
            }
            _column.AddView(rsvp, Margins(context, padding, padding));
        }

        // Detail rows.
        AddRow(context, WinoIconGlyph.Location, item.Location);
        if (item.IsRecurringEvent) AddRow(context, WinoIconGlyph.CalendarEventRepeat, CalendarEventDisplay.FormatRecurrence(item.CalendarItem.Recurrence, item.Period.Start, item.Period.End, item.IsAllDayEvent, settings));
        if (item.AssignedCalendar is { } calendar)
        {
            string? account = calendar.MailAccount?.Name;
            AddRow(context, WinoIconGlyph.Calendar, string.IsNullOrEmpty(account) ? calendar.Name : $"{calendar.Name} · {account}");
        }

        // Show as and Reminder: chosen in a sheet and saved through the shared Save command.
        var showAs = AddRow(context, WinoIconGlyph.CalendarShowAs, viewModel.SelectedShowAsOption?.DisplayText ?? Translator.CalendarEventDetails_ShowAs, Translator.CalendarEventDetails_ShowAs);
        if (showAs is not null && !readOnly) showAs.OnTap(() => PickShowAs(context, viewModel));
        var selectedReminders = viewModel.ReminderOptions.Where(option => option.IsSelected).Select(option => option.DisplayText).ToList();
        var reminder = AddRow(context, WinoIconGlyph.Reminder, selectedReminders.Count == 0 ? Translator.CalendarReminder_None : string.Join(", ", selectedReminders), Translator.CalendarEventDetails_Reminder);
        if (reminder is not null && !readOnly) reminder.OnTap(() => PickReminder(context, viewModel));

        // People.
        if (item.Attendees.Count > 0)
        {
            _column.AddView(CalendarFormRow.Divider(context));
            AddSubheader(context, $"{Translator.CalendarEventDetails_People} · {item.Attendees.Count}");
            foreach (var attendee in item.Attendees)
            {
                var row = new CalendarFormRow(context, WinoIconGlyph.Person, compact: true)
                {
                    Text = string.IsNullOrWhiteSpace(attendee.Name) ? attendee.Email : attendee.Name,
                    Supporting = attendee.IsOrganizer ? Translator.CalendarEventDetails_Organizer : CalendarEventDisplay.GetAttendeeStatusText(attendee.AttendenceStatus)
                };
                _column.AddView(row);
            }
        }

        // Notes: the description as rich text (no remote content).
        if (!string.IsNullOrWhiteSpace(item.CalendarItem.Description))
        {
            _column.AddView(CalendarFormRow.Divider(context));
            AddSubheader(context, Translator.CalendarEventCompose_Notes);
            var notes = WinoTokens.Style(new TextView(context), WinoTextStyle.Body);
            notes.SetText(HtmlCompat(item.CalendarItem.Description), TextView.BufferType.Spannable);
            notes.SetTextIsSelectable(true);
            _column.AddView(notes, Margins(context, padding, 0));
        }

        // Attachments.
        if (viewModel.HasAttachments)
        {
            _column.AddView(CalendarFormRow.Divider(context));
            AddSubheader(context, Translator.CalendarEventDetails_Attachments);
            var chips = new ChipGroup(context);
            foreach (var attachment in viewModel.Attachments)
            {
                var chip = new Chip(context) { Text = attachment.FileName };
                chip.Click += (_, _) => { if (viewModel.OpenAttachmentCommand.CanExecute(attachment)) viewModel.OpenAttachmentCommand.Execute(attachment); };
                chips.AddView(chip);
            }
            _column.AddView(chips, Margins(context, padding, 0));
        }
    }

    private CalendarFormRow? AddRow(Context context, WinoIconGlyph glyph, string? text, string? label = null)
    {
        if (_column is null || string.IsNullOrWhiteSpace(text)) return null;
        var row = new CalendarFormRow(context, glyph) { Text = text, Supporting = label };
        row.ContentDescription = label is null ? text : $"{label}: {text}";
        _column.AddView(row);
        return row;
    }

    private void AddSubheader(Context context, string text)
    {
        var subheader = new TextView(context) { Text = text, Gravity = GravityFlags.CenterVertical };
        subheader.SetTextAppearance(Resource.Style.TextAppearance_WinoKit_Label);
        subheader.SetTextColor(WinoColors.AccentText(context));
        int padding = WinoTokens.Px(context, Resource.Dimension.wino_m3_subheader_padding);
        subheader.SetPadding(padding, 0, padding, 0);
        AndroidX.Core.View.ViewCompat.SetAccessibilityHeading(subheader, true);
        _column?.AddView(subheader, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, WinoTokens.Px(context, Resource.Dimension.wino_m3_subheader_height)));
    }

    private static LinearLayout.LayoutParams Margins(Context context, int horizontal, int bottom)
        => new(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { MarginStart = horizontal, MarginEnd = horizontal, BottomMargin = bottom };

    private static ISpanned? HtmlCompat(string html) => AndroidX.Core.Text.HtmlCompat.FromHtml(html, AndroidX.Core.Text.HtmlCompat.FromHtmlModeCompact);

    private void PickShowAs(Context context, EventDetailsPageViewModel viewModel)
    {
        var options = viewModel.ShowAsOptions.ToList();
        WinoChoiceSheet.Show(context, Translator.CalendarEventDetails_ShowAs, options.Select(option => option.DisplayText).ToList(),
            options.IndexOf(viewModel.SelectedShowAsOption), index =>
            {
                if (index < 0 || index >= options.Count) return;
                viewModel.SelectedShowAsOption = options[index];
                Execute(viewModel.SaveCommand);
            });
    }

    /// <summary>Reminders are a multi-choice in Windows; the sheet toggles one option per pick and saves.</summary>
    private void PickReminder(Context context, EventDetailsPageViewModel viewModel)
    {
        var options = viewModel.ReminderOptions.ToList();
        WinoChoiceSheet.Show(context, Translator.CalendarEventDetails_Reminder,
            options.Select(option => option.IsSelected ? $"✓ {option.DisplayText}" : option.DisplayText).ToList(), -1, index =>
            {
                if (index < 0 || index >= options.Count) return;
                options[index].IsSelected = !options[index].IsSelected;
                Execute(viewModel.SaveCommand);
                if (ViewModel is { } current && Entry is { } entry) Rebuild(context, current, entry.Dispatcher);
            });
    }

    private void Execute(System.Windows.Input.ICommand command)
    {
        if (command.CanExecute(null)) command.Execute(null);
    }

    /// <summary>Back on compact widths; Close in the details pane releases the page and the selection.</summary>
    private void Close()
    {
        if (_compact)
        {
            Activity?.OnBackPressedDispatcher.OnBackPressed();
            return;
        }
        _ = MainApplication.Services?.GetService<AndroidNavigationService>()?.CloseRenderingFrameAsync();
    }
}
