using System.Collections.Specialized;
using CoreGraphics;
using Foundation;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.UIKit.Extras;
using Wino.Mail.UIKit.Views.Calendar;
using Wino.Presentation.UIKit;
using Wino.Mail.ViewModels.Calendar;
using Wino.Mail.ViewModels.Calendar.Data;

namespace Wino.Mail.UIKit.Phone.Calendar;

/// <summary>
/// The lists of the calendar page. The agenda ("Show as list"; calendar refresh of 14 Nov 2026): the loaded events of the
/// visible range grouped by day under sticky day headers ("Today · ", "Tomorrow · "), with the week strip above it in
/// Day and Week. A row reads: start time over the duration in a leading column, the calendar colour bar, then the title
/// over the location; all-day rows carry a light tint of their colour and rows of a day are split by hairlines. Day and
/// Week list every day of the range ("No events" under an empty day); Month lists only the days with events. The compact
/// month's day list shows the selected day's rows under a header with "Day ›", which opens that day.
/// A row tap opens the details like a tile tap; a long press shows the shared context menu.
/// </summary>
public sealed partial class CalendarPageViewController
{
    private readonly UITableView _agenda = new(CGRect.Empty, UITableViewStyle.Plain) { TranslatesAutoresizingMaskIntoConstraints = false, Hidden = true };
    private readonly WinoStateView _agendaEmpty = new(WinoIconGlyph.CalendarAgenda, Translator.IOS_Calendar_NoEvents, null, WinoStyle.TertiaryText) { Hidden = true };
    private readonly List<CalendarAgendaDay> _agendaDays = [];
    private readonly List<CalendarAgendaDay> _monthDays = [];
    private readonly UITableView _monthList = new(CGRect.Empty, UITableViewStyle.Plain) { TranslatesAutoresizingMaskIntoConstraints = false };
    private readonly UILabel _monthDayTitle = WinoStyle.Label(string.Empty, WinoStyle.Scaled(13, UIFontWeight.Semibold));
    private readonly UIButton _monthOpenDay = new() { TranslatesAutoresizingMaskIntoConstraints = false };
    private AgendaSource? _agendaSource;
    private AgendaSource? _monthSource;
    private INotifyCollectionChanged? _observedItems;
    private bool _agendaReloadQueued;

    private void BuildAgenda(UIView content)
    {
        _agenda.BackgroundColor = UIColor.Clear;
        _agenda.SectionHeaderTopPadding = 0;
        _agenda.SeparatorStyle = UITableViewCellSeparatorStyle.None;
        _agenda.RegisterClassForHeaderFooterViewReuse(typeof(AgendaDayHeader), AgendaDayHeader.Identifier);
        _agenda.RowHeight = UITableView.AutomaticDimension;
        _agenda.EstimatedRowHeight = 58;
        _agenda.RegisterClassForCellReuse(typeof(AgendaCell), AgendaCell.Identifier);
        _agendaSource = new AgendaSource(this, _agendaDays, headers: true);
        _agenda.Source = _agendaSource;
        WinoAccessibility.Label(_agenda, Translator.IOS_Calendar_Agenda);
        WinoLayout.Fill(_agenda, content);
        _agendaEmpty.Place(content);
    }

    /// <summary>The compact month's day list: a 40pt header with the day and "Day ›", then the day's agenda rows.</summary>
    private void BuildMonthList(UIView slot)
    {
        var configuration = UIButtonConfiguration.PlainButtonConfiguration;
        configuration.AttributedTitle = new NSAttributedString(Translator.IOS_Calendar_OpenDay,
            new UIStringAttributes { Font = WinoStyle.Fixed(13, UIFontWeight.Semibold), ForegroundColor = WinoStyle.Accent });
        configuration.Image = WinoIcons.Image(WinoIconGlyph.ChevronRight, 10, WinoStyle.Accent);
        configuration.ImagePlacement = NSDirectionalRectEdge.Trailing;
        configuration.ImagePadding = 3;
        configuration.ContentInsets = new NSDirectionalEdgeInsets(0, 8, 0, 4);
        _monthOpenDay.Configuration = configuration;
        _monthOpenDay.AccessibilityHint = Translator.IOS_Calendar_OpenDayHint;
        _monthOpenDay.PrimaryActionTriggered += (_, _) => { if (_monthSelection is { } date) OpenDay(date); };
        _monthDayTitle.AccessibilityTraits = UIAccessibilityTrait.Header;
        WinoLayout.Priorities(_monthDayTitle, UILayoutConstraintAxis.Horizontal, 1, 250);
        var header = WinoLayout.HStack(8, _monthDayTitle, _monthOpenDay);
        header.Alignment = UIStackViewAlignment.Center;
        slot.AddSubview(header);

        _monthList.BackgroundColor = UIColor.Clear;
        _monthList.SeparatorStyle = UITableViewCellSeparatorStyle.None;
        _monthList.SectionHeaderTopPadding = 0;
        _monthList.RowHeight = UITableView.AutomaticDimension;
        _monthList.EstimatedRowHeight = 58;
        _monthList.RegisterClassForCellReuse(typeof(AgendaCell), AgendaCell.Identifier);
        _monthSource = new AgendaSource(this, _monthDays, headers: false);
        _monthList.Source = _monthSource;
        slot.AddSubview(_monthList);
        NSLayoutConstraint.ActivateConstraints(
        [
            header.TopAnchor.ConstraintEqualTo(slot.TopAnchor),
            header.LeadingAnchor.ConstraintEqualTo(slot.LeadingAnchor, 16),
            header.TrailingAnchor.ConstraintEqualTo(slot.TrailingAnchor, -12),
            header.HeightAnchor.ConstraintEqualTo(40),
            _monthList.TopAnchor.ConstraintEqualTo(header.BottomAnchor),
            _monthList.LeadingAnchor.ConstraintEqualTo(slot.LeadingAnchor),
            _monthList.TrailingAnchor.ConstraintEqualTo(slot.TrailingAnchor),
            _monthList.BottomAnchor.ConstraintEqualTo(slot.BottomAnchor)
        ]);
    }

    private void SetMonthListHidden(bool hidden)
    {
        if (_page is { } page) page.MonthListSlot.Hidden = hidden;
    }

    /// <summary>The selected day's rows under the compact month (a short fade when the day changes).</summary>
    private void ReloadMonthList()
    {
        _monthDays.Clear();
        if (_monthSelection is not { } date)
        {
            _monthList.ReloadData();
            return;
        }
        _monthDays.AddRange(CalendarAgenda.BuildDays([date], ViewModel.CalendarItems.ToList(), skipEmptyDays: false));
        bool today = date == DateContext.GetToday();
        _monthDayTitle.Text = DayTitle(date);
        _monthDayTitle.TextColor = today ? WinoStyle.Accent : WinoStyle.SecondaryText;
        _monthOpenDay.AccessibilityLabel = $"{Translator.IOS_Calendar_OpenDay}, {date.ToDateTime(TimeOnly.MinValue).ToString("D", System.Globalization.CultureInfo.CurrentCulture)}";
        if (_monthList.Window is not null && !UIAccessibility.IsReduceMotionEnabled)
            UIView.Transition(_monthList, 0.15, UIViewAnimationOptions.TransitionCrossDissolve, _monthList.ReloadData, null);
        else _monthList.ReloadData();
    }

    private void SetAgendaHidden(bool hidden)
    {
        _agenda.Hidden = hidden;
        _agendaEmpty.Hidden = hidden || _agendaDays.Count > 0;
    }

    private void ObserveAgendaItems()
    {
        if (_observedItems is not null) _observedItems.CollectionChanged -= AgendaItemsChanged;
        _observedItems = ViewModel.CalendarItems;
        if (_observedItems is not null) _observedItems.CollectionChanged += AgendaItemsChanged;
    }

    private void AgendaItemsChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (_agendaReloadQueued) return;
        _agendaReloadQueued = true;
        BeginInvokeOnMainThread(() =>
        {
            _agendaReloadQueued = false;
            if (!Released) ReloadAgenda();
        });
    }

    private void ReloadAgenda()
    {
        _agendaDays.Clear();
        // Shared agenda rule: Day and Week list every day of the range, Month only the days with events.
        if (ViewModel.CurrentVisibleRange is { } range)
            _agendaDays.AddRange(CalendarAgenda.BuildDays(range.Dates, ViewModel.CalendarItems.ToList(), skipEmptyDays: range.DisplayType == CalendarDisplayType.Month));
        if (!_agenda.Hidden) _agendaEmpty.Hidden = _agendaDays.Any(day => day.Items.Count > 0);
        _agenda.ReloadData();
        ReloadMonthList();
    }

    private void ReleaseAgenda()
    {
        if (_observedItems is not null) _observedItems.CollectionChanged -= AgendaItemsChanged;
        _observedItems = null;
    }

    private string AgendaTime(CalendarItemViewModel item, DateOnly date) => CalendarAgenda.TimeText(item, date, ViewModel.CurrentSettings);

    private string AgendaStart(CalendarItemViewModel item, DateOnly date) => CalendarAgenda.StartText(item, date, ViewModel.CurrentSettings);

    /// <summary>Scrolls the agenda to <paramref name="date"/> when the list holds it (a week strip tap in the agenda).</summary>
    private bool ScrollAgendaTo(DateOnly date)
    {
        int section = _agendaDays.FindIndex(day => day.Date == date);
        if (_agenda.Hidden || section < 0) return false;
        _agenda.ScrollToRow(NSIndexPath.FromRowSection(0, section), UITableViewScrollPosition.Top, true);
        return true;
    }

    /// <summary>
    /// "Today · Saturday, Nov 14", "Tomorrow · Sunday, Nov 15" or "Thursday, Nov 12". The shared
    /// <c>CalendarAgenda.DayTitle</c> has no Tomorrow prefix and a different date format, so the phone builds its own.
    /// </summary>
    private string DayTitle(DateOnly date)
    {
        var culture = ViewModel.CurrentSettings?.CultureInfo ?? System.Globalization.CultureInfo.CurrentCulture;
        var today = DateContext.GetToday();
        var text = date.ToDateTime(TimeOnly.MinValue).ToString("dddd, MMM d", culture);
        if (date == today) return string.Format(culture, Translator.IOS_Calendar_TodayPrefix, text);
        if (date == today.AddDays(1)) return string.Format(culture, Translator.IOS_Calendar_TomorrowPrefix, text);
        return text;
    }

    private sealed class AgendaSource(CalendarPageViewController owner, List<CalendarAgendaDay> days, bool headers) : UITableViewSource
    {
        public override nint NumberOfSections(UITableView tableView) => days.Count;

        public override nint RowsInSection(UITableView tableview, nint section)
            => section < days.Count ? Math.Max(1, days[(int)section].Items.Count) : 0;

        public override nfloat GetHeightForHeader(UITableView tableView, nint section) => headers ? AgendaDayHeader.Height : 0;

        public override UIView? GetViewForHeader(UITableView tableView, nint section)
        {
            if (!headers || section >= days.Count) return null;
            if (tableView.DequeueReusableHeaderFooterView(AgendaDayHeader.Identifier) is not AgendaDayHeader header)
                header = new AgendaDayHeader(new NSString(AgendaDayHeader.Identifier));
            var date = days[(int)section].Date;
            header.Configure(owner.DayTitle(date), isToday: date == owner.DateContext.GetToday(), first: section == 0);
            return header;
        }

        public override UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath)
        {
            var cell = (AgendaCell)tableView.DequeueReusableCell(AgendaCell.Identifier, indexPath);
            var (date, items) = days[indexPath.Section];
            if (indexPath.Row < items.Count)
            {
                var item = items[indexPath.Row];
                cell.Configure(item.Title ?? string.Empty, owner.AgendaStart(item, date), CalendarAgenda.DurationText(item, date),
                    owner.AgendaTime(item, date), item.Location, CalendarTileMapper.Fill(item), CalendarAgenda.IsAllDayOn(item, date),
                    item.IsSelected, separated: indexPath.Row > 0);
            }
            else
            {
                cell.ConfigureEmpty(Translator.IOS_Calendar_NoEvents);
            }
            return cell;
        }

        public override void RowSelected(UITableView tableView, NSIndexPath indexPath)
        {
            tableView.DeselectRow(indexPath, true);
            if (Item(indexPath) is { } item) owner.SelectItem(item);
        }

        public override UIContextMenuConfiguration? GetContextMenuConfiguration(UITableView tableView, NSIndexPath indexPath, CGPoint point)
            => Item(indexPath) is { } item
                ? UIContextMenuConfiguration.Create(null, null, _ => owner.ContextMenu(item))
                : null;

        private CalendarItemViewModel? Item(NSIndexPath indexPath)
        {
            if (indexPath.Section >= days.Count) return null;
            var items = days[indexPath.Section].Items;
            return indexPath.Row < items.Count ? items[indexPath.Row] : null;
        }
    }

    /// <summary>A sticky day header: the day in 13pt semibold (today in the accent, other days secondary) under a hairline.</summary>
    private sealed class AgendaDayHeader : UITableViewHeaderFooterView
    {
        public const string Identifier = "WinoCalendarAgendaDay";
        public const float Height = 40;
        private readonly UILabel _title = WinoStyle.Label(string.Empty, WinoStyle.Scaled(13, UIFontWeight.Semibold));
        private readonly UIView _rule = new() { TranslatesAutoresizingMaskIntoConstraints = false, BackgroundColor = AgendaStyle.Hairline };

        public AgendaDayHeader(NSString identifier) : base(identifier) => Build();

        public AgendaDayHeader(ObjCRuntime.NativeHandle handle) : base(handle) => Build();

        private void Build()
        {
            // The zone surface behind the title: a pinned header still covers the rows under it.
            BackgroundConfiguration = UIBackgroundConfiguration.ClearConfiguration;
            ContentView.BackgroundColor = WinoStyle.ZoneFill;
            ContentView.AddSubview(_title);
            ContentView.AddSubview(_rule);
            NSLayoutConstraint.ActivateConstraints(
            [
                _rule.TopAnchor.ConstraintEqualTo(ContentView.TopAnchor),
                _rule.LeadingAnchor.ConstraintEqualTo(ContentView.LeadingAnchor),
                _rule.TrailingAnchor.ConstraintEqualTo(ContentView.TrailingAnchor),
                _rule.HeightAnchor.ConstraintEqualTo(1 / UIScreen.MainScreen.Scale),
                _title.LeadingAnchor.ConstraintEqualTo(ContentView.LeadingAnchor, 16),
                _title.TrailingAnchor.ConstraintLessThanOrEqualTo(ContentView.TrailingAnchor, -12),
                _title.BottomAnchor.ConstraintEqualTo(ContentView.BottomAnchor, -6)
            ]);
            IsAccessibilityElement = true;
            AccessibilityTraits = UIAccessibilityTrait.Header;
        }

        public void Configure(string title, bool isToday, bool first)
        {
            _title.Text = title;
            _title.TextColor = isToday ? WinoStyle.Accent : WinoStyle.SecondaryText;
            _rule.Hidden = first;
            AccessibilityLabel = title;
        }
    }

    /// <summary>Metrics of the agenda rows (Calendar board).</summary>
    private static class AgendaStyle
    {
        public static UIColor Hairline => WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.09), WinoStyle.Hex(0xFFFFFF, 0.11));
        public const double TimeColumnWidth = 62;
        public const double BarHeight = 34;
        public const double MinimumRowHeight = 58;
        public const double EmptyRowHeight = 36;
        public const double AllDayTint = 0.07;
    }

    /// <summary>
    /// One agenda row: the start time over the duration (or "All day") in a leading column, the calendar colour bar, then
    /// the title over the location; a hairline above every row but a day's first.
    /// </summary>
    private sealed class AgendaCell : UITableViewCell
    {
        public const string Identifier = "WinoCalendarAgendaCell";
        private readonly WinoSurfaceView _bar = new() { CornerRadius = 2 };
        private readonly UILabel _start = WinoStyle.Label(string.Empty, WinoStyle.Description, WinoStyle.SecondaryText);
        private readonly UILabel _duration = WinoStyle.Label(string.Empty, WinoStyle.Scaled(11), WinoStyle.SecondaryText);
        private readonly UILabel _title = WinoStyle.Label(string.Empty, WinoStyle.Scaled(15, UIFontWeight.Semibold), WinoStyle.PrimaryText, 2);
        private readonly UILabel _location = WinoStyle.Label(string.Empty, WinoStyle.Description, WinoStyle.SecondaryText);
        private readonly UIStackView _timeColumn;
        private readonly UIView _rule = new() { TranslatesAutoresizingMaskIntoConstraints = false, BackgroundColor = AgendaStyle.Hairline };
        private readonly NSLayoutConstraint _minimum;

        public AgendaCell(ObjCRuntime.NativeHandle handle) : base(handle)
        {
            BackgroundColor = UIColor.Clear;
            WinoLayout.Size(_bar, 4, AgendaStyle.BarHeight);
            _timeColumn = WinoLayout.VStack(0, _start, _duration);
            _timeColumn.Alignment = UIStackViewAlignment.Leading;
            WinoLayout.Size(_timeColumn, AgendaStyle.TimeColumnWidth, -1);
            var text = WinoLayout.VStack(1, _title, _location);
            text.Alignment = UIStackViewAlignment.Leading;
            WinoLayout.Priorities(text, UILayoutConstraintAxis.Horizontal, 1, 250);
            var row = WinoLayout.Insets(WinoLayout.HStack(10, _timeColumn, _bar, text), 8, 16, 8, 16);
            row.Alignment = UIStackViewAlignment.Center;
            WinoLayout.Fill(row, ContentView);
            _minimum = ContentView.HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)AgendaStyle.MinimumRowHeight);
            _minimum.Priority = 999;
            _minimum.Active = true;
            ContentView.AddSubview(_rule);
            NSLayoutConstraint.ActivateConstraints(
            [
                _rule.TopAnchor.ConstraintEqualTo(ContentView.TopAnchor),
                _rule.LeadingAnchor.ConstraintEqualTo(ContentView.LeadingAnchor),
                _rule.TrailingAnchor.ConstraintEqualTo(ContentView.TrailingAnchor),
                _rule.HeightAnchor.ConstraintEqualTo(1 / UIScreen.MainScreen.Scale)
            ]);
        }

        public void Configure(string title, string start, string duration, string time, string? location, UIColor fill, bool allDay, bool selected, bool separated)
        {
            _timeColumn.Alpha = 1;
            _bar.Alpha = 1;
            _minimum.Constant = (nfloat)AgendaStyle.MinimumRowHeight;
            _bar.Fill = fill;
            _title.Text = title;
            _title.Font = WinoStyle.Scaled(15, UIFontWeight.Semibold);
            _title.TextColor = WinoStyle.PrimaryText;
            _start.Text = start;
            _duration.Text = duration;
            _duration.Hidden = string.IsNullOrEmpty(duration);
            _location.Text = location ?? string.Empty;
            _location.Hidden = string.IsNullOrWhiteSpace(location);
            _rule.Hidden = !separated;
            BackgroundColor = selected ? WinoStyle.SelectedFill : allDay ? fill.ColorWithAlpha((nfloat)AgendaStyle.AllDayTint) : UIColor.Clear;
            SelectionStyle = UITableViewCellSelectionStyle.Default;
            IsAccessibilityElement = true;
            AccessibilityLabel = string.IsNullOrWhiteSpace(location) ? $"{title}, {time}" : $"{title}, {time}, {location}";
        }

        public void ConfigureEmpty(string text)
        {
            // The time column and bar stay in place, invisible, so "No events" lines up with the titles.
            _timeColumn.Alpha = 0;
            _bar.Alpha = 0;
            _start.Text = string.Empty;
            _duration.Hidden = true;
            _minimum.Constant = (nfloat)AgendaStyle.EmptyRowHeight;
            _title.Text = text;
            _title.Font = WinoStyle.Scaled(14);
            _title.TextColor = WinoStyle.TertiaryText;
            _location.Hidden = true;
            _rule.Hidden = true;
            BackgroundColor = UIColor.Clear;
            SelectionStyle = UITableViewCellSelectionStyle.None;
            AccessibilityLabel = text;
        }
    }
}
