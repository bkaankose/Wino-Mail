using System.Collections.Specialized;
using CoreGraphics;
using Foundation;
using UIKit;
using Wino.Calendar.ViewModels;
using Wino.Calendar.ViewModels.Data;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.UIKit.Extras;
using Wino.Mail.UIKit.Views.Calendar;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Phone.Calendar;

/// <summary>
/// The agenda list of the iPhone calendar (the canvas "agenda" toggle): the loaded events of the visible range grouped by
/// day under sticky day headers. Day and Week list every day of the range; Month lists only the days with events.
/// A row tap opens the details like a tile tap; a long press shows the shared context menu.
/// </summary>
public sealed partial class CalendarPageViewController
{
    private readonly UITableView _agenda = new(CGRect.Empty, UITableViewStyle.Plain) { TranslatesAutoresizingMaskIntoConstraints = false, Hidden = true };
    private readonly WinoStateView _agendaEmpty = new(WinoIconGlyph.CalendarAgenda, Translator.IOS_Calendar_NoEvents, null, WinoStyle.TertiaryText) { Hidden = true };
    private readonly List<CalendarAgendaDay> _agendaDays = [];
    private AgendaSource? _agendaSource;
    private INotifyCollectionChanged? _observedItems;
    private bool _agendaReloadQueued;

    private void BuildAgenda(UIView content)
    {
        _agenda.BackgroundColor = UIColor.Clear;
        _agenda.SectionHeaderTopPadding = 0;
        _agenda.RowHeight = UITableView.AutomaticDimension;
        _agenda.EstimatedRowHeight = 58;
        _agenda.RegisterClassForCellReuse(typeof(AgendaCell), AgendaCell.Identifier);
        _agendaSource = new AgendaSource(this);
        _agenda.Source = _agendaSource;
        WinoAccessibility.Label(_agenda, Translator.IOS_Calendar_Agenda);
        WinoLayout.Fill(_agenda, content);
        _agendaEmpty.Place(content);
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
    }

    private void ReleaseAgenda()
    {
        if (_observedItems is not null) _observedItems.CollectionChanged -= AgendaItemsChanged;
        _observedItems = null;
    }

    private string AgendaTime(CalendarItemViewModel item, DateOnly date) => CalendarAgenda.TimeText(item, date, ViewModel.CurrentSettings);

    private string DayTitle(DateOnly date) => CalendarAgenda.DayTitle(date, DateContext.GetToday(), ViewModel.CurrentSettings?.CultureInfo);

    private sealed class AgendaSource(CalendarPageViewController owner) : UITableViewSource
    {
        public override nint NumberOfSections(UITableView tableView) => owner._agendaDays.Count;

        public override nint RowsInSection(UITableView tableview, nint section)
            => section < owner._agendaDays.Count ? Math.Max(1, owner._agendaDays[(int)section].Items.Count) : 0;

        public override string? TitleForHeader(UITableView tableView, nint section)
            => section < owner._agendaDays.Count ? owner.DayTitle(owner._agendaDays[(int)section].Date) : string.Empty;

        public override UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath)
        {
            var cell = (AgendaCell)tableView.DequeueReusableCell(AgendaCell.Identifier, indexPath);
            var (date, items) = owner._agendaDays[indexPath.Section];
            if (indexPath.Row < items.Count)
            {
                var item = items[indexPath.Row];
                cell.Configure(item.Title ?? string.Empty, owner.AgendaTime(item, date), item.Location, CalendarTileMapper.Fill(item), item.IsSelected);
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
            if (indexPath.Section >= owner._agendaDays.Count) return null;
            var items = owner._agendaDays[indexPath.Section].Items;
            return indexPath.Row < items.Count ? items[indexPath.Row] : null;
        }
    }

    /// <summary>One agenda row: the calendar colour bar, the title, the time (or "All day") and the location.</summary>
    private sealed class AgendaCell : UITableViewCell
    {
        public const string Identifier = "WinoCalendarAgendaCell";
        private readonly WinoSurfaceView _bar = new() { CornerRadius = 2 };
        private readonly UILabel _title = WinoStyle.Label(string.Empty, WinoStyle.BodyMedium, WinoStyle.PrimaryText, 2);
        private readonly UILabel _time = WinoStyle.Label(string.Empty, WinoStyle.Subheadline, WinoStyle.SecondaryText);
        private readonly UILabel _location = WinoStyle.Label(string.Empty, WinoStyle.Caption, WinoStyle.SecondaryText);

        public AgendaCell(ObjCRuntime.NativeHandle handle) : base(handle)
        {
            BackgroundColor = UIColor.Clear;
            WinoLayout.Size(_bar, 4, -1);
            var text = WinoLayout.VStack(2, _title, _time, _location);
            text.Alignment = UIStackViewAlignment.Leading;
            var row = WinoLayout.Insets(WinoLayout.HStack(12, _bar, text), 8, 16, 8, 16);
            row.Alignment = UIStackViewAlignment.Fill;
            WinoLayout.Fill(row, ContentView);
        }

        public void Configure(string title, string time, string? location, UIColor fill, bool selected)
        {
            _bar.Hidden = false;
            _bar.Fill = fill;
            _title.Text = title;
            _title.TextColor = WinoStyle.PrimaryText;
            _time.Text = time;
            _time.Hidden = false;
            _location.Text = location ?? string.Empty;
            _location.Hidden = string.IsNullOrWhiteSpace(location);
            BackgroundColor = selected ? WinoStyle.SelectedFill : UIColor.Clear;
            SelectionStyle = UITableViewCellSelectionStyle.Default;
            IsAccessibilityElement = true;
            AccessibilityLabel = string.IsNullOrWhiteSpace(location) ? $"{title}, {time}" : $"{title}, {time}, {location}";
        }

        public void ConfigureEmpty(string text)
        {
            _bar.Hidden = true;
            _title.Text = text;
            _title.TextColor = WinoStyle.TertiaryText;
            _time.Hidden = true;
            _location.Hidden = true;
            BackgroundColor = UIColor.Clear;
            SelectionStyle = UITableViewCellSelectionStyle.None;
            AccessibilityLabel = text;
        }
    }
}
