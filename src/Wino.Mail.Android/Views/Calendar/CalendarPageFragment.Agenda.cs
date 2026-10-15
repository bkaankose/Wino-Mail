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
/// Compact widths (canvas Material pass boards Calendar and CalendarMonth): the top app bar title is the month and
/// switches between the agenda (week strip over the week's days with their events) and the month grid (event dots, the
/// chosen day's events below). Previous and next follow the strip or grid flings and, in the month view, the app bar
/// arrows. Rules (which events fall on a day, their order, texts and dots) come from the shared <see cref="CalendarAgenda"/>.
/// </summary>
public sealed partial class CalendarPageFragment
{
    private const int MenuToday = 1;
    private const int MenuPrevious = 2;
    private const int MenuNext = 3;
    private const int MenuSettings = 4;

    private TextView? _title;
    private WinoCalendarWeekStripView? _weekStrip;
    private WinoCalendarMonthGridView? _monthGrid;
    private RecyclerView? _agendaList;
    private AgendaAdapter? _agenda;
    private DateOnly? _selectedDate;
    private bool _isMonth;

    private void BuildAgenda(View view, Context context, CalendarPageViewModel viewModel)
    {
        _title = view.FindViewById<TextView>(Resource.Id.calendar_title);
        _weekStrip = view.FindViewById<WinoCalendarWeekStripView>(Resource.Id.calendar_week_strip);
        _monthGrid = view.FindViewById<WinoCalendarMonthGridView>(Resource.Id.calendar_month_grid);
        _agendaList = view.FindViewById<RecyclerView>(Resource.Id.calendar_agenda);

        if (_title is not null)
        {
            _title.SetCompoundDrawablesRelativeWithIntrinsicBounds(0, 0, Resource.Drawable.wino_ic_arrow_drop_down, 0);
            _title.Click += (_, _) => RequestDisplayType(_isMonth ? CalendarDisplayType.Week : CalendarDisplayType.Month);
        }
        if (_toolbar is not null)
        {
            _toolbar.MenuItemClick += (_, args) =>
            {
                switch (args.Item?.ItemId)
                {
                    case MenuToday: GoToday(); break;
                    case MenuPrevious: GoPrevious(); break;
                    case MenuNext: GoNext(); break;
                    case MenuSettings: _navigation?.Shell?.OpenSettings(); break;
                }
            };
        }
        if (_weekStrip is not null)
        {
            _weekStrip.DateClicked += (_, date) => SelectDate(date, scroll: true);
            _weekStrip.SwipedNext += (_, _) => GoNext();
            _weekStrip.SwipedPrevious += (_, _) => GoPrevious();
        }
        if (_monthGrid is not null)
        {
            _monthGrid.DateClicked += (_, date) =>
            {
                // A day of the neighbouring month opens that month (Windows CalendarView).
                if (ViewModel?.CurrentVisibleRange is { } range && !range.Contains(date)) GoToDateOnShell(date);
                SelectDate(date, scroll: false);
            };
            _monthGrid.SwipedNext += (_, _) => GoNext();
            _monthGrid.SwipedPrevious += (_, _) => GoPrevious();
            _monthGrid.DotsProvider = date => ViewModel is { } vm
                ? CalendarAgenda.DayDots(date, vm.CalendarItems).Select(hex => WinoColors.FromHexString(hex) ?? WinoColors.Accent(context)).ToList()
                : [];
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
        BuildAgendaMenu(context);
    }

    private void BuildAgendaMenu(Context context)
    {
        if (_toolbar is null || _title is null) return;
        var menu = _toolbar.Menu;
        menu.Clear();
        float glyph = (float)WinoTokens.Dp(context, Resource.Dimension.wino_m3_glyph);
        var secondary = WinoColors.SecondaryText(context);
        if (_isMonth)
        {
            menu.Add(0, MenuPrevious, 0, Translator.Accessibility_PreviousCalendarPeriod)?.SetIcon(WinoIcons.Drawable(context, WinoIconGlyph.ArrowLeft, glyph, secondary))?.SetShowAsAction(ShowAsAction.Always);
            menu.Add(0, MenuNext, 1, Translator.Accessibility_NextCalendarPeriod)?.SetIcon(WinoIcons.Drawable(context, WinoIconGlyph.ArrowRight, glyph, secondary))?.SetShowAsAction(ShowAsAction.Always);
        }
        else
        {
            menu.Add(0, MenuToday, 0, Translator.Today)?.SetIcon(WinoIcons.Drawable(context, WinoIconGlyph.CalendarToday, glyph, secondary))?.SetShowAsAction(ShowAsAction.Always);
        }
        if (_isMonth) menu.Add(0, MenuToday, 3, Translator.Today);
        menu.Add(0, MenuSettings, 4, Translator.MenuSettings);
    }

    private void GoToDateOnShell(DateOnly date)
    {
        var args = new CalendarViewDayClickedEventArgs(date.ToDateTime(TimeOnly.MinValue));
        if (_shell?.DateClickedCommand.CanExecute(args) == true) _shell.DateClickedCommand.Execute(args);
    }

    private void AgendaDisplayTypeChanged(CalendarDisplayType type)
    {
        if (_title?.Context is not { } context) return;
        bool month = type == CalendarDisplayType.Month;
        if (month == _isMonth && _toolbar?.Menu?.Size() > 0) return;
        _isMonth = month;
        if (_weekStrip is not null) _weekStrip.Visibility = month ? ViewStates.Gone : ViewStates.Visible;
        if (_monthGrid is not null) _monthGrid.Visibility = month ? ViewStates.Visible : ViewStates.Gone;
        BuildAgendaMenu(context);
        ReloadAgenda();
    }

    private void AgendaRangeChanged(VisibleDateRange? range)
    {
        if (_title is null || range is null) return;
        var today = TodayDate;
        // Keep the chosen day while it stays in view; otherwise today when shown, else the first day.
        if (_selectedDate is not { } selected || !range.Contains(selected))
            _selectedDate = range.Contains(today) ? today : range.DisplayType == CalendarDisplayType.Month ? new DateOnly(range.AnchorDate.Year, range.AnchorDate.Month, 1) : range.StartDate;
        if (_weekStrip is not null)
        {
            _weekStrip.Today = today;
            _weekStrip.Dates = range.DisplayType == CalendarDisplayType.Month ? [] : range.Dates;
            _weekStrip.SelectedDate = _selectedDate;
        }
        if (_monthGrid is not null)
        {
            _monthGrid.Today = today;
            _monthGrid.DisplayMonth = range.AnchorDate;
            _monthGrid.SelectedDate = _selectedDate;
        }
        UpdateTitle();
        ReloadAgenda();
        ScrollAgendaTo(_selectedDate);
    }

    private void AgendaSettingsChanged(CalendarSettings? settings)
    {
        if (_title is null || settings is null) return;
        if (_monthGrid is not null)
        {
            _monthGrid.FirstDayOfWeek = settings.FirstDayOfWeek;
            _monthGrid.Culture = settings.CultureInfo;
        }
        if (_weekStrip is not null) _weekStrip.Culture = settings.CultureInfo;
        UpdateTitle();
        ReloadAgenda();
    }

    private void AgendaItemsChanged()
    {
        if (_title is null) return;
        _monthGrid?.RefreshDots();
        ReloadAgenda();
    }

    private void AgendaSelectionChanged() => _agenda?.NotifyDataSetChanged();

    private void SelectDate(DateOnly date, bool scroll)
    {
        _selectedDate = date;
        if (_weekStrip is not null) _weekStrip.SelectedDate = date;
        if (_monthGrid is not null) _monthGrid.SelectedDate = date;
        UpdateTitle();
        if (_isMonth) ReloadAgenda();
        if (scroll) ScrollAgendaTo(date);
    }

    private void UpdateTitle()
    {
        if (_title is null) return;
        var date = _selectedDate ?? ViewModel?.CurrentVisibleRange?.AnchorDate ?? TodayDate;
        if (_isMonth && ViewModel?.CurrentVisibleRange is { } range) date = range.AnchorDate;
        _title.Text = CalendarAgenda.MonthTitle(date, ViewModel?.CurrentSettings?.CultureInfo);
        _title.ContentDescription = _title.Text;
    }

    private void ReloadAgenda()
    {
        if (_agenda is null || ViewModel is not { } viewModel) return;
        var range = viewModel.CurrentVisibleRange;
        IEnumerable<DateOnly> dates = range is null ? [] : _isMonth ? (_selectedDate is { } day ? [day] : []) : range.Dates;
        _agenda.SetDays(CalendarAgenda.BuildDays(dates, viewModel.CalendarItems.ToList(), skipEmptyDays: false));
    }

    private void ScrollAgendaTo(DateOnly? date)
    {
        if (date is not { } day || _agenda is null || _agendaList?.GetLayoutManager() is not LinearLayoutManager layout || _isMonth) return;
        int position = _agenda.PositionOf(day);
        if (position >= 0) layout.ScrollToPositionWithOffset(position, 0);
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
