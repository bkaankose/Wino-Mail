using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;
using AndroidX.RecyclerView.Widget;
using Google.Android.Material.FloatingActionButton;
using Wino.Calendar.ViewModels;
using Wino.Calendar.ViewModels.Data;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Calendar;
using Wino.Mail.Controls.Android.Calendar;
using Wino.Presentation.Android;

namespace Wino.Mail.Android.Views.Calendar;

/// <summary>
/// Compact widths (date picker option B, 15 Oct 2026; the iPhone CalendarPageViewController): the app bar title is the
/// visible range with a dropdown arrow and opens the date picker sheet (<see cref="CalendarPickerSheet"/>: Day · Week ·
/// Month, the agenda toggle, the calendars filter, Today and the month grid). Day view shows the week strip in its own
/// small zone, and so does the agenda of a day or week (canvas Material pass Calendar board); Week and Month grids and the
/// month agenda start right under the bar. The content zone holds the Windows calendar surface or, with the agenda
/// toggle on, the agenda list of the visible range. A horizontal drag on the surface moves its rail
/// through the periods with the finger; on the strip it moves a week. Month cells show only their event count; tapping a day opens it in Day view. Rules
/// (which events fall on a day, their order and texts) come from the shared <see cref="CalendarAgenda"/>.
/// </summary>
public sealed partial class CalendarPageFragment : ICalendarPickerHost
{
    private const int MenuToday = 1;
    private const int MenuSettings = 4;
    private const string PickerTag = "calendar_picker";

    private TextView? _title;
    private View? _stripZone;
    private WinoCalendarWeekStripView? _weekStrip;
    private RecyclerView? _agendaList;
    private AgendaAdapter? _agenda;
    private bool _agendaOn;
    private CalendarDisplayType _displayType = CalendarDisplayType.Week;

    private bool IsPhone => _title is not null;

    private void BuildPhone(View view, Context context, CalendarPageViewModel viewModel)
    {
        _title = view.FindViewById<TextView>(Resource.Id.calendar_title);
        _stripZone = view.FindViewById(Resource.Id.calendar_strip_zone);
        _weekStrip = view.FindViewById<WinoCalendarWeekStripView>(Resource.Id.calendar_week_strip);
        _agendaList = view.FindViewById<RecyclerView>(Resource.Id.calendar_agenda);
        _displayType = viewModel.StatePersistanceService.CalendarDisplayType;

        BuildSurfaceCore(view, context);

        if (_title is not null)
        {
            _title.SetCompoundDrawablesRelativeWithIntrinsicBounds(0, 0, Resource.Drawable.wino_ic_arrow_drop_down, 0);
            _title.Click += (_, _) => ShowPicker();
            AndroidX.Core.View.ViewCompat.SetTooltipText(_title, Translator.IOS_Calendar_ChooseDate);
            AndroidX.Core.View.ViewCompat.SetAccessibilityDelegate(_title, new TitleAccessibility());
        }
        if (_toolbar is not null)
        {
            var menu = _toolbar.Menu;
            menu.Clear();
            float glyph = (float)WinoTokens.Dp(context, Resource.Dimension.wino_m3_glyph);
            menu.Add(0, MenuToday, 0, Translator.Today)?.SetIcon(WinoIcons.Drawable(context, WinoIconGlyph.CalendarToday, glyph, WinoColors.SecondaryText(context)))?.SetShowAsAction(ShowAsAction.Always);
            menu.Add(0, MenuSettings, 1, Translator.MenuSettings);
            _toolbar.MenuItemClick += (_, args) =>
            {
                switch (args.Item?.ItemId)
                {
                    case MenuToday: GoToday(); break;
                    case MenuSettings: _navigation?.Shell?.OpenSettings(); break;
                }
            };
        }
        if (_weekStrip is not null)
        {
            _weekStrip.DateClicked += (_, date) => GoToDateOnShell(date);
            _weekStrip.SwipedNext += (_, _) => ShiftWeek(7);
            _weekStrip.SwipedPrevious += (_, _) => ShiftWeek(-7);
        }
        if (_agendaList is not null)
        {
            _agendaList.SetLayoutManager(new LinearLayoutManager(context));
            _agenda = new AgendaAdapter(this);
            _agendaList.SetAdapter(_agenda);
        }
        if (view.FindViewById<FloatingActionButton>(Resource.Id.calendar_fab) is { } fab)
        {
            fab.SetImageDrawable(WinoIcons.Drawable(context, WinoIconGlyph.Add, WinoTokens.Dp(context, Resource.Dimension.wino_m3_fab_glyph), WinoColors.OnPrimaryContainer(context)));
            WinoMaterialAccent.Tint(fab);
            fab.ContentDescription = Translator.CalendarEventCompose_NewEventButton;
            fab.Click += (_, _) => NewEvent();
        }
        // Pull to refresh waits for the top of whichever list shows.
        _refresh?.SetOnChildScrollUpCallback(new ScrollUpCallback(() => _agendaOn
            ? _agendaList?.CanScrollVertically(-1) ?? false
            : _surface?.CanScrollUp ?? false));
        ApplyContentVisibility();
        ApplyStripVisibility();
    }

    private void GoToDateOnShell(DateOnly date)
    {
        var args = new CalendarViewDayClickedEventArgs(date.ToDateTime(TimeOnly.MinValue));
        if (_shell?.DateClickedCommand.CanExecute(args) == true) _shell.DateClickedCommand.Execute(args);
    }

    /// <summary>The week strip moves a week; the day stays the same weekday.</summary>
    private void ShiftWeek(int days)
    {
        if (ViewModel?.CurrentVisibleRange is { } range) GoToDateOnShell(range.AnchorDate.AddDays(days));
    }

    /// <summary>A month cell opens its day in Day view (the cell shows only the count); other empty slots open the composer.</summary>
    private void PhoneSlotClicked(WinoCalendarSlotEventArgs args)
    {
        if (ViewModel?.CurrentVisibleRange?.DisplayType != CalendarDisplayType.Month || _shell is null)
        {
            OpenComposerAt(args.Start, args.IsAllDay);
            return;
        }
        _shell.SetDisplayTypeWithoutNavigation(CalendarDisplayType.Day);
        GoToDateOnShell(DateOnly.FromDateTime(args.Start));
    }

    // ---- The date picker sheet ----

    private void ShowPicker()
    {
        if (!IsAdded || ChildFragmentManager.IsStateSaved || Picker is not null) return;
        new CalendarPickerSheet().Show(ChildFragmentManager, PickerTag);
    }

    private CalendarPickerSheet? Picker => IsAdded ? ChildFragmentManager.FindFragmentByTag(PickerTag) as CalendarPickerSheet : null;

    CalendarDisplayType ICalendarPickerHost.DisplayType => _displayType;
    bool ICalendarPickerHost.IsAgendaOn => _agendaOn;
    VisibleDateRange? ICalendarPickerHost.VisibleRange => ViewModel?.CurrentVisibleRange;
    DayOfWeek ICalendarPickerHost.FirstDayOfWeek => FirstDayOfWeek;
    DateOnly ICalendarPickerHost.Today => TodayDate;
    void ICalendarPickerHost.PickDisplayType(CalendarDisplayType type) => RequestDisplayType(type);
    void ICalendarPickerHost.PickAgenda(bool on) => SetAgenda(on);
    void ICalendarPickerHost.PickToday() => GoToday();
    void ICalendarPickerHost.PickDate(DateOnly date) => GoToDateOnShell(date);
    // The calendars filter is the navigation drawer's calendar list on phones; it replaces the sheet.
    void ICalendarPickerHost.PickCalendars() => Chrome?.ToggleNavigation();

    private DayOfWeek FirstDayOfWeek => ViewModel?.CurrentSettings?.FirstDayOfWeek ?? System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;

    // ---- Page state ----

    private void SetAgenda(bool on)
    {
        _agendaOn = on;
        ApplyContentVisibility();
        ApplyStripVisibility();
        if (on) ReloadAgenda(scrollToToday: true);
    }

    private void ApplyContentVisibility()
    {
        if (!IsPhone) return;
        if (_surface is not null) _surface.Visibility = _agendaOn ? ViewStates.Invisible : ViewStates.Visible;
        if (_agendaList is not null) _agendaList.Visibility = _agendaOn ? ViewStates.Visible : ViewStates.Gone;
    }

    private void PhoneDisplayTypeChanged(CalendarDisplayType type)
    {
        if (!IsPhone) return;
        _displayType = type;
        ApplyStripVisibility();
        Picker?.Refresh();
    }

    /// <summary>
    /// Day view and the day or week agenda keep the week strip under the bar (canvas Calendar board: the strip over the
    /// agenda); the Week and Month grids and the month agenda give its height to the content.
    /// </summary>
    private void ApplyStripVisibility()
    {
        if (_stripZone is null) return;
        bool strip = _displayType == CalendarDisplayType.Day || (_agendaOn && _displayType != CalendarDisplayType.Month);
        _stripZone.Visibility = strip ? ViewStates.Visible : ViewStates.Gone;
    }

    private void PhoneRangeChanged(VisibleDateRange? range)
    {
        if (!IsPhone || range is null) return;
        if (_weekStrip is not null)
        {
            var anchor = range.AnchorDate;
            var start = anchor.AddDays(-(((int)anchor.DayOfWeek - (int)FirstDayOfWeek + 7) % 7));
            _weekStrip.Today = TodayDate;
            _weekStrip.Dates = Enumerable.Range(0, 7).Select(start.AddDays).ToList();
            _weekStrip.SelectedDate = anchor;
        }
        ReloadAgenda(scrollToToday: true);
        Picker?.Refresh();
    }

    private void PhoneRangeTextChanged(string? text)
    {
        if (_title is null) return;
        _title.Text = string.IsNullOrWhiteSpace(text) ? Translator.KeyboardShortcuts_ModeCalendar : text;
        _title.ContentDescription = _title.Text;
    }

    private void PhoneSettingsChanged(CalendarSettings? settings)
    {
        if (!IsPhone || settings is null) return;
        if (_weekStrip is not null) _weekStrip.Culture = settings.CultureInfo;
        PhoneRangeChanged(ViewModel?.CurrentVisibleRange);
    }

    private void PhoneItemsChanged()
    {
        if (IsPhone && _agendaOn) ReloadAgenda(scrollToToday: false);
    }

    private void PhoneSelectionChanged() => _agenda?.NotifyDataSetChanged();

    /// <summary>The agenda of the visible range: every day of a week, the days with events of a month.</summary>
    private void ReloadAgenda(bool scrollToToday)
    {
        if (_agenda is null || !_agendaOn || ViewModel is not { } viewModel) return;
        var range = viewModel.CurrentVisibleRange;
        bool month = range?.DisplayType == CalendarDisplayType.Month;
        IEnumerable<DateOnly> dates = range is null ? []
            : month ? range.Dates.Where(date => date.Month == range.AnchorDate.Month && date.Year == range.AnchorDate.Year)
            : range.Dates;
        _agenda.SetDays(CalendarAgenda.BuildDays(dates, viewModel.CalendarItems.ToList(), skipEmptyDays: month));
        if (!scrollToToday || _agendaList?.GetLayoutManager() is not LinearLayoutManager layout) return;
        // The day picked on the week strip leads; otherwise today, when the range shows it.
        int position = range is not null && !month ? _agenda.PositionOf(range.AnchorDate) : -1;
        if (position < 0) position = _agenda.PositionOf(TodayDate);
        layout.ScrollToPositionWithOffset(Math.Max(0, position), 0);
    }

    /// <summary>The title announces itself as a button that chooses a date.</summary>
    private sealed class TitleAccessibility : AndroidX.Core.View.AccessibilityDelegateCompat
    {
        public override void OnInitializeAccessibilityNodeInfo(View host, AndroidX.Core.View.Accessibility.AccessibilityNodeInfoCompat info)
        {
            base.OnInitializeAccessibilityNodeInfo(host, info);
            info.ClassName = "android.widget.Button";
            info.AddAction(new AndroidX.Core.View.Accessibility.AccessibilityNodeInfoCompat.AccessibilityActionCompat(
                (int)global::Android.Views.Accessibility.Action.Click, Translator.IOS_Calendar_ChooseDate));
        }
    }

    // ---- Agenda list ----

    private sealed class AgendaAdapter(CalendarPageFragment owner) : RecyclerView.Adapter
    {
        private const int HeaderType = 0;
        private const int EventType = 1;
        private const int EmptyType = 2;
        private readonly List<(int Type, DateOnly Date, CalendarItemViewModel? Item)> _rows = [];

        public void SetDays(IReadOnlyList<CalendarAgendaDay> days)
        {
            _rows.Clear();
            foreach (var day in days)
            {
                _rows.Add((HeaderType, day.Date, null));
                if (day.Items.Count == 0) _rows.Add((EmptyType, day.Date, null));
                foreach (var item in day.Items) _rows.Add((EventType, day.Date, item));
            }
            NotifyDataSetChanged();
        }

        public int PositionOf(DateOnly date) => _rows.FindIndex(row => row.Type == HeaderType && row.Date == date);

        public override int ItemCount => _rows.Count;

        public override int GetItemViewType(int position) => _rows[position].Type;

        public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType)
        {
            var context = parent.RequireContext();
            if (viewType == EventType)
            {
                var row = LayoutInflater.From(context)!.Inflate(Resource.Layout.calendar_agenda_row, parent, false)!;
                var holder = new Holder(row);
                row.Click += (_, _) =>
                {
                    int position = holder.BindingAdapterPosition;
                    if (position >= 0 && position < _rows.Count && _rows[position].Item is { } item) owner.SelectItem(item);
                };
                return holder;
            }
            var text = new TextView(context);
            if (viewType == HeaderType)
            {
                text.SetTextAppearance(Resource.Style.TextAppearance_WinoKit_Label);
                text.Gravity = GravityFlags.CenterVertical;
                text.SetMaxLines(1);
                text.Ellipsize = global::Android.Text.TextUtils.TruncateAt.End;
                int padding = WinoTokens.Px(context, Resource.Dimension.wino_m3_subheader_padding);
                text.SetPadding(padding, 0, padding, 0);
                text.LayoutParameters = new RecyclerView.LayoutParams(ViewGroup.LayoutParams.MatchParent, WinoTokens.Px(context, Resource.Dimension.wino_m3_subheader_height));
                ViewCompatAccessibility.Heading(text);
            }
            else
            {
                text.SetTextAppearance(Resource.Style.TextAppearance_WinoKit_BodySecondary);
                int start = WinoTokens.Px(context, Resource.Dimension.wino_m3_subheader_padding) + WinoTokens.Px(context, Resource.Dimension.wino_calendar_agenda_time_width) + WinoTokens.Px(context, Resource.Dimension.wino_calendar_agenda_gap);
                text.SetPadding(start, 0, WinoTokens.Px(context, Resource.Dimension.wino_m3_subheader_padding), 0);
                text.Gravity = GravityFlags.CenterVertical;
                text.LayoutParameters = new RecyclerView.LayoutParams(ViewGroup.LayoutParams.MatchParent, WinoTokens.Px(context, Resource.Dimension.wino_size_touch_large));
            }
            return new Holder(text);
        }

        public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position)
        {
            var (type, date, item) = _rows[position];
            var view = holder.ItemView;
            var context = view.RequireContext();
            switch (type)
            {
                case HeaderType when view is TextView header:
                    var today = owner.TodayDate;
                    header.Text = CalendarAgenda.DayTitle(date, today, owner.ViewModel?.CurrentSettings?.CultureInfo);
                    header.SetTextColor(date == today ? WinoColors.AccentText(context) : WinoColors.SecondaryText(context));
                    break;
                case EmptyType when view is TextView empty:
                    empty.Text = Translator.IOS_Calendar_NoEvents;
                    break;
                case EventType when item is not null:
                    BindEvent(view, context, item, date);
                    break;
            }
        }

        private void BindEvent(View view, Context context, CalendarItemViewModel item, DateOnly date)
        {
            var settings = owner.ViewModel?.CurrentSettings;
            string start = CalendarAgenda.StartText(item, date, settings);
            string duration = CalendarAgenda.DurationText(item, date);
            if (view.FindViewById<TextView>(Resource.Id.agenda_time) is { } time) time.Text = string.IsNullOrEmpty(duration) ? start : $"{start}\n{duration}";
            if (view.FindViewById<TextView>(Resource.Id.agenda_title) is { } title) title.Text = item.Title ?? string.Empty;
            if (view.FindViewById<TextView>(Resource.Id.agenda_supporting) is { } supporting)
            {
                supporting.Text = item.Location ?? string.Empty;
                supporting.Visibility = string.IsNullOrWhiteSpace(item.Location) ? ViewStates.Gone : ViewStates.Visible;
            }
            if (view.FindViewById(Resource.Id.agenda_card) is { } card)
            {
                var color = CalendarTiles.Fill(context, item);
                var fill = new GradientDrawable();
                fill.SetCornerRadius(WinoTokens.PxF(context, Resource.Dimension.wino_calendar_agenda_card_radius));
                fill.SetColor(WinoColors.Blend(color, WinoColors.ZoneFill(context), WinoColors.Fraction(context, Resource.Fraction.wino_calendar_agenda_card_tint)));
                if (item.IsSelected) fill.SetStroke(WinoTokens.Px(context, Resource.Dimension.wino_calendar_tile_selected_stroke), color);
                card.Background = fill;
            }
            view.ContentDescription = string.Join(", ", new[] { item.Title, CalendarAgenda.TimeText(item, date, settings), item.Location }.Where(part => !string.IsNullOrWhiteSpace(part)));
        }

        private sealed class Holder(View view) : RecyclerView.ViewHolder(view);
    }
}

/// <summary>Marks list subheaders as headings for TalkBack (API 28+).</summary>
internal static class ViewCompatAccessibility
{
    public static void Heading(View view) => AndroidX.Core.View.ViewCompat.SetAccessibilityHeading(view, true);
}
