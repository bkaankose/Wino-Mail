using Android.Content;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Views;
using Android.Widget;
using Google.Android.Material.BottomSheet;
using Google.Android.Material.Button;
using Wino.Calendar.ViewModels;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Calendar;
using Wino.Mail.Controls.Android.Calendar;
using Wino.Presentation.Android;

namespace Wino.Mail.Android.Views.Calendar;

/// <summary>What the date picker sheet asks of the phone calendar page (the iPhone ICalendarPickerHost).</summary>
internal interface ICalendarPickerHost
{
    CalendarDisplayType DisplayType { get; }
    bool IsAgendaOn { get; }
    VisibleDateRange? VisibleRange { get; }
    DayOfWeek FirstDayOfWeek { get; }
    DateOnly Today { get; }
    void PickDisplayType(CalendarDisplayType type);
    void PickAgenda(bool on);
    void PickToday();
    void PickDate(DateOnly date);
    void PickCalendars();
}

/// <summary>
/// The phone calendar's date picker sheet (option B, 15 Oct 2026; the iPhone CalendarPickerSheetViewController), opened
/// from the app bar title: Day · Week · Month, the agenda toggle and the calendars filter in one row, Today, and the Wino
/// month grid with the visible range highlighted. Picking a day goes there (the shell's DateClicked) and closes the sheet;
/// the period, agenda and Today keep it open; the calendars filter replaces it with the calendars in the navigation
/// drawer. The page (<see cref="CalendarPageFragment"/>, the parent fragment) is the host, so the sheet survives
/// recreation with it.
/// </summary>
public sealed class CalendarPickerSheet : BottomSheetDialogFragment
{
    /// <summary>The phone segments; Work week shows as Week.</summary>
    internal static readonly CalendarDisplayType[] DisplayTypes = [CalendarDisplayType.Day, CalendarDisplayType.Week, CalendarDisplayType.Month];

    private MaterialButtonToggleGroup? _types;
    private WinoIconView? _agenda;
    private TextView? _month;
    private WinoCalendarMonthGridView? _grid;
    private bool _applying;

    private ICalendarPickerHost? PickerHost => ParentFragment as ICalendarPickerHost;

    public override View? OnCreateView(LayoutInflater inflater, ViewGroup? container, Bundle? savedInstanceState)
    {
        var view = inflater.Inflate(Resource.Layout.calendar_picker_sheet, container, false);
        if (view?.Context is not { } context) return view;
        _types = view.FindViewById<MaterialButtonToggleGroup>(Resource.Id.picker_types);
        _agenda = view.FindViewById<WinoIconView>(Resource.Id.picker_agenda);
        _month = view.FindViewById<TextView>(Resource.Id.picker_month);
        _grid = view.FindViewById<WinoCalendarMonthGridView>(Resource.Id.picker_grid);

        if (_types is not null)
        {
            foreach (var type in DisplayTypes)
            {
                if (inflater.Inflate(Resource.Layout.mail_pivot_segment, _types, false) is not MaterialButton button) continue;
                button.Id = View.GenerateViewId();
                button.Text = type switch
                {
                    CalendarDisplayType.Day => Translator.CalendarTypeSelector_Day,
                    CalendarDisplayType.Month => Translator.CalendarTypeSelector_Month,
                    _ => Translator.CalendarTypeSelector_Week
                };
                button.Tag = (int)type;
                WinoMaterialAccent.TintM3Segment(button);
                _types.AddView(button, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1));
            }
            _types.AddOnButtonCheckedListener(new TypeListener(this));
        }

        if (_agenda is not null)
        {
            Glyph(context, _agenda, WinoIconGlyph.CalendarAgenda, Translator.IOS_Calendar_Agenda);
            _agenda.Click += (_, _) =>
            {
                if (PickerHost is not { } host) return;
                host.PickAgenda(!host.IsAgendaOn);
                Refresh();
            };
        }
        if (view.FindViewById<WinoIconView>(Resource.Id.picker_calendars) is { } calendars)
        {
            Glyph(context, calendars, WinoIconGlyph.Filter, Translator.IOS_Calendar_Calendars);
            calendars.Click += (_, _) =>
            {
                var host = PickerHost;
                Dismiss();
                host?.PickCalendars();
            };
        }
        if (view.FindViewById<MaterialButton>(Resource.Id.picker_today) is { } today)
        {
            today.Text = Translator.Today;
            today.Click += (_, _) => PickerHost?.PickToday();
        }
        if (view.FindViewById<WinoIconView>(Resource.Id.picker_previous) is { } previous)
        {
            Glyph(context, previous, WinoIconGlyph.ArrowLeft, Translator.Accessibility_PreviousCalendarPeriod);
            previous.Click += (_, _) => Browse(-1);
        }
        if (view.FindViewById<WinoIconView>(Resource.Id.picker_next) is { } next)
        {
            Glyph(context, next, WinoIconGlyph.ArrowRight, Translator.Accessibility_NextCalendarPeriod);
            next.Click += (_, _) => Browse(1);
        }
        if (_grid is not null)
        {
            _grid.IsCompact = true;
            _grid.DateClicked += (_, date) =>
            {
                PickerHost?.PickDate(date);
                Dismiss();
            };
            _grid.SwipedNext += (_, _) => Browse(1);
            _grid.SwipedPrevious += (_, _) => Browse(-1);
        }
        return view;
    }

    public override void OnStart()
    {
        base.OnStart();
        // No page to drive (the process came back without it): nothing to pick.
        if (PickerHost is null)
        {
            DismissAllowingStateLoss();
            return;
        }
        Refresh();
    }

    public override void OnDestroyView()
    {
        _types = null;
        _agenda = null;
        _month = null;
        _grid = null;
        base.OnDestroyView();
    }

    /// <summary>Reads the page state again (the range, period or agenda changed while the sheet is open).</summary>
    internal void Refresh()
    {
        if (PickerHost is not { } host || Context is not { } context) return;
        if (_types is not null)
        {
            var shown = host.DisplayType == CalendarDisplayType.WorkWeek ? CalendarDisplayType.Week : host.DisplayType;
            _applying = true;
            try
            {
                for (int index = 0; index < _types.ChildCount; index++)
                {
                    if (_types.GetChildAt(index) is not MaterialButton button || button.Tag is not Java.Lang.Integer tag) continue;
                    bool selected = tag.IntValue() == (int)shown;
                    // No check glyph: three segments beside two icon buttons leave no room for it at phone width.
                    if (selected) _types.Check(button.Id);
                }
            }
            finally { _applying = false; }
        }
        if (_agenda is not null)
        {
            bool on = host.IsAgendaOn;
            _agenda.SetTextColor(on ? WinoColors.AccentText(context) : WinoColors.SecondaryText(context));
            var fill = new GradientDrawable();
            fill.SetShape(ShapeType.Oval);
            fill.SetColor(on ? WinoColors.SecondaryContainer(context) : global::Android.Graphics.Color.Transparent);
            _agenda.Background = new RippleDrawable(global::Android.Content.Res.ColorStateList.ValueOf(WinoColors.SubtleFill(context)), fill, null);
            _agenda.Selected = on;
            AndroidX.Core.View.ViewCompat.SetStateDescription(_agenda, on ? Translator.SemanticIndex_On : Translator.SemanticIndex_Off);
        }
        if (_grid is not null)
        {
            _grid.Today = host.Today;
            _grid.FirstDayOfWeek = host.FirstDayOfWeek;
            if (host.VisibleRange is { } range)
            {
                _grid.HighlightedDates = new HashSet<DateOnly>(range.Dates);
                _grid.DisplayMonth = range.AnchorDate;
            }
            UpdateMonthTitle();
        }
    }

    /// <summary>Browses months in the sheet without changing the calendar's range (Windows CalendarView month arrows).</summary>
    private void Browse(int months)
    {
        if (_grid is null) return;
        _grid.DisplayMonth = _grid.DisplayMonth.AddMonths(months);
        UpdateMonthTitle();
    }

    private void UpdateMonthTitle()
    {
        if (_month is not null && _grid is not null) _month.Text = CalendarAgenda.MonthTitle(_grid.DisplayMonth, null);
    }

    private void TypeChecked(MaterialButtonToggleGroup? group, int checkedId, bool isChecked)
    {
        if (_applying || !isChecked || group?.FindViewById<MaterialButton>(checkedId)?.Tag is not Java.Lang.Integer tag) return;
        PickerHost?.PickDisplayType((CalendarDisplayType)tag.IntValue());
    }

    private static void Glyph(Context context, WinoIconView view, WinoIconGlyph glyph, string label)
    {
        view.Icon = glyph;
        view.GlyphSize = WinoTokens.Dp(context, Resource.Dimension.wino_glyph_m);
        view.ContentDescription = label;
        view.Clickable = true;
        view.Focusable = true;
    }

    private sealed class TypeListener(CalendarPickerSheet owner) : Java.Lang.Object, MaterialButtonToggleGroup.IOnButtonCheckedListener
    {
        public void OnButtonChecked(MaterialButtonToggleGroup? group, int checkedId, bool isChecked) => owner.TypeChecked(group, checkedId, isChecked);
    }
}
