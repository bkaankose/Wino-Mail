using System.ComponentModel;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;
using Wino.Calendar.ViewModels.Data;
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.MenuItems;
using Wino.Core.Domain.Models.Calendar;
using Wino.Mail.Controls.Android.Calendar;
using Wino.Presentation.Android;
using Wino.Presentation.Controls;
using Wino.Core.Domain.Enums;
using IMenuItem = Wino.Core.Domain.Interfaces.IMenuItem;

namespace Wino.Mail.Android.Views.Calendar;

/// <summary>
/// The calendar pane rows of the shell menu (canvas board CalendarTablet; Windows ShellCalendarView and the calendar
/// group templates): the date picker over the shell's visible range, and the calendars with their colour check boxes.
/// <see cref="Shell.ShellMenuAdapter"/> asks <see cref="ViewTypeOf"/> and hosts these views; everything else keeps the
/// Wino shell row.
/// </summary>
internal static class CalendarPaneRows
{
    public const int DatePickerType = 1;
    public const int CalendarType = 2;
    public const int GroupType = 3;

    /// <summary>The row type of a calendar pane item, or 0 for a regular shell row.</summary>
    public static int ViewTypeOf(IMenuItem item) => item switch
    {
        CalendarDatePickerMenuItem => DatePickerType,
        UngroupedCalendarMenuItem => CalendarType,
        AccountCalendarGroupMenuItem => GroupType,
        _ => 0
    };

    public static View Create(Context context, int viewType) => viewType switch
    {
        DatePickerType => new DatePickerRow(context),
        GroupType => new GroupRow(context),
        _ => new CalendarCheckRow(context)
    };

    /// <summary>Binds <paramref name="item"/> to a view made by <see cref="Create"/>.</summary>
    public static void Bind(View view, IMenuItem item)
    {
        switch (view)
        {
            case DatePickerRow picker when item is CalendarDatePickerMenuItem date: picker.Bind(date.Parameter); break;
            case CalendarCheckRow row when item is UngroupedCalendarMenuItem calendar: row.Bind(calendar.Parameter, toggles: false); break;
            case GroupRow group when item is AccountCalendarGroupMenuItem account: group.Bind(account.Parameter); break;
        }
    }

    /// <summary>The month picker over the calendar shell's visible range; a day navigates there (Windows DateClicked).</summary>
    private sealed class DatePickerRow : LinearLayout
    {
        private readonly TextView _title;
        private readonly WinoCalendarMonthGridView _grid;
        private ICalendarShellClient? _client;

        public DatePickerRow(Context context) : base(context)
        {
            Orientation = Orientation.Vertical;
            int padding = WinoTokens.Px(context, Resource.Dimension.wino_calendar_pane_padding);
            SetPadding(padding, 0, padding, padding);
            var header = new LinearLayout(context) { Orientation = Orientation.Horizontal };
            header.SetGravity(GravityFlags.CenterVertical);
            _title = WinoTokens.Style(new TextView(context), WinoTextStyle.BodyStrong);
            header.AddView(_title, new LayoutParams(0, WinoTokens.Px(context, Resource.Dimension.wino_calendar_pane_header_height), 1) { Gravity = GravityFlags.CenterVertical });
            _title.Gravity = GravityFlags.CenterVertical;
            header.AddView(Button(context, WinoIconGlyph.ArrowLeft, Translator.Accessibility_PreviousCalendarPeriod, () => Move(-1)));
            header.AddView(Button(context, WinoIconGlyph.ArrowRight, Translator.Accessibility_NextCalendarPeriod, () => Move(1)));
            AddView(header, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
            _grid = new WinoCalendarMonthGridView(context) { IsCompact = true };
            _grid.DateClicked += (_, date) =>
            {
                if (_client is null) return;
                var args = new CalendarViewDayClickedEventArgs(date.ToDateTime(TimeOnly.MinValue));
                if (_client.DateClickedCommand.CanExecute(args)) _client.DateClickedCommand.Execute(args);
            };
            _grid.SwipedNext += (_, _) => Move(1);
            _grid.SwipedPrevious += (_, _) => Move(-1);
            AddView(_grid, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        }

        private WinoIconView Button(Context context, WinoIconGlyph glyph, string description, Action action)
        {
            var button = new WinoIconView(context) { Icon = glyph, GlyphSize = WinoTokens.Dp(context, Resource.Dimension.wino_glyph_m), ContentDescription = description, Clickable = true, Gravity = GravityFlags.Center };
            button.SetTextColor(WinoColors.SecondaryText(context));
            button.SetBackgroundResource(Shell.ShellViews.BorderlessRipple(context));
            button.Click += (_, _) => action();
            int size = WinoTokens.Px(context, Resource.Dimension.wino_size_control);
            button.LayoutParameters = new LayoutParams(size, size);
            return button;
        }

        /// <summary>Browses months without changing the calendar's range (Windows CalendarView month arrows).</summary>
        private void Move(int months)
        {
            _grid.DisplayMonth = _grid.DisplayMonth.AddMonths(months);
            UpdateTitle();
        }

        public void Bind(ICalendarShellClient? client)
        {
            if (!ReferenceEquals(_client, client))
            {
                if (_client is not null) _client.PropertyChanged -= ClientChanged;
                _client = client;
                if (client is not null) client.PropertyChanged += ClientChanged;
            }
            Synchronize();
        }

        protected override void OnDetachedFromWindow()
        {
            if (_client is not null) _client.PropertyChanged -= ClientChanged;
            _client = null;
            base.OnDetachedFromWindow();
        }

        private void ClientChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName is null or "" or nameof(ICalendarShellClient.CurrentVisibleRange)) Post(Synchronize);
        }

        private void Synchronize()
        {
            var range = _client?.CurrentVisibleRange;
            _grid.Today = DateOnly.FromDateTime(DateTime.Today);
            _grid.FirstDayOfWeek = MainApplication.Services?.GetService(typeof(IPreferencesService)) is IPreferencesService preferences
                ? preferences.FirstDayOfWeek
                : System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
            _grid.HighlightedDates = range is null ? new HashSet<DateOnly>() : new HashSet<DateOnly>(range.Dates);
            if (range is not null) _grid.DisplayMonth = range.AnchorDate;
            UpdateTitle();
        }

        private void UpdateTitle() => _title.Text = Wino.Calendar.ViewModels.CalendarAgenda.MonthTitle(_grid.DisplayMonth, null);
    }

    /// <summary>One calendar: a check box in the calendar colour and the name (Windows calendar check box template).</summary>
    private sealed class CalendarCheckRow : LinearLayout
    {
        private readonly View _check;
        private readonly TextView _name;
        private AccountCalendarViewModel? _calendar;
        private bool _toggles;

        public CalendarCheckRow(Context context) : base(context)
        {
            Orientation = Orientation.Horizontal;
            SetGravity(GravityFlags.CenterVertical);
            int padding = WinoTokens.Px(context, Resource.Dimension.wino_m3_drawer_item_padding_start);
            SetPadding(padding, 0, WinoTokens.Px(context, Resource.Dimension.wino_calendar_pane_padding), 0);
            SetMinimumHeight(WinoTokens.Px(context, Resource.Dimension.wino_calendar_pane_row_height));
            int check = WinoTokens.Px(context, Resource.Dimension.wino_calendar_pane_check);
            _check = new View(context);
            AddView(_check, new LayoutParams(check, check) { MarginEnd = WinoTokens.Px(context, Resource.Dimension.wino_m3_drawer_item_icon_gap) });
            _name = WinoTokens.Style(new TextView(context) { Ellipsize = global::Android.Text.TextUtils.TruncateAt.End }, WinoTextStyle.Body);
            _name.SetMaxLines(1);
            AddView(_name, new LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1));
            SetBackgroundResource(Shell.ShellViews.Ripple(context));
            Click += (_, _) => { if (_toggles && _calendar is { } calendar) calendar.IsChecked = !calendar.IsChecked; };
        }

        /// <summary>
        /// <paramref name="toggles"/>: the row flips the calendar itself (inside an account group); otherwise the shell
        /// menu's invocation does (UngroupedCalendarMenuItem.Toggle).
        /// </summary>
        public void Bind(AccountCalendarViewModel calendar, bool toggles)
        {
            if (_calendar is not null) _calendar.PropertyChanged -= CalendarChanged;
            _calendar = calendar;
            _toggles = toggles;
            Clickable = toggles;
            calendar.PropertyChanged += CalendarChanged;
            Apply();
        }

        protected override void OnDetachedFromWindow()
        {
            if (_calendar is not null) _calendar.PropertyChanged -= CalendarChanged;
            base.OnDetachedFromWindow();
        }

        protected override void OnAttachedToWindow()
        {
            base.OnAttachedToWindow();
            if (_calendar is { } calendar)
            {
                calendar.PropertyChanged -= CalendarChanged;
                calendar.PropertyChanged += CalendarChanged;
                Apply();
            }
        }

        private void CalendarChanged(object? sender, PropertyChangedEventArgs args) => Post(Apply);

        private void Apply()
        {
            if (_calendar is not { } calendar || Context is not { } context) return;
            var color = WinoColors.FromHexString(calendar.BackgroundColorHex) ?? WinoColors.Accent(context);
            var box = new GradientDrawable();
            box.SetCornerRadius(WinoTokens.PxF(context, Resource.Dimension.wino_calendar_pane_check_radius));
            if (calendar.IsChecked) box.SetColor(color);
            else box.SetStroke(WinoTokens.Px(context, Resource.Dimension.wino_stroke_selection) / 2 + 1, color);
            _check.Background = calendar.IsChecked
                ? new LayerDrawable([box, WinoIcons.Drawable(context, WinoIconGlyph.Checkmark, WinoTokens.Dp(context, Resource.Dimension.wino_glyph_xs), WinoColors.ReadableText(color))])
                : box;
            _name.Text = calendar.Name;
            ContentDescription = calendar.Name;
            ApplyStateDescription(calendar.IsChecked);
        }

        private void ApplyStateDescription(bool isChecked)
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(30)) base.StateDescription = isChecked ? Translator.Buttons_Yes : Translator.Buttons_No;
        }
    }

    /// <summary>One account with its calendars (Windows AccountCalendarGroupTemplate, grouped calendar pane).</summary>
    private sealed class GroupRow : LinearLayout
    {
        private readonly TextView _name;
        private readonly TextView _address;
        private readonly LinearLayout _calendars;
        private GroupedAccountCalendarViewModel? _group;

        public GroupRow(Context context) : base(context)
        {
            Orientation = Orientation.Vertical;
            var header = new LinearLayout(context) { Orientation = Orientation.Vertical };
            int padding = WinoTokens.Px(context, Resource.Dimension.wino_m3_drawer_item_padding_start);
            header.SetPadding(padding, WinoTokens.Px(context, Resource.Dimension.wino_space_s), padding, WinoTokens.Px(context, Resource.Dimension.wino_space_2xs));
            _name = WinoTokens.Style(new TextView(context), WinoTextStyle.BodyStrong);
            _address = WinoTokens.Style(new TextView(context), WinoTextStyle.Caption);
            header.AddView(_name);
            header.AddView(_address);
            AddView(header);
            _calendars = new LinearLayout(context) { Orientation = Orientation.Vertical };
            AddView(_calendars, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        }

        public void Bind(GroupedAccountCalendarViewModel group)
        {
            if (_group is not null) _group.AccountCalendars.CollectionChanged -= CalendarsChanged;
            _group = group;
            group.AccountCalendars.CollectionChanged += CalendarsChanged;
            Rebuild();
        }

        protected override void OnDetachedFromWindow()
        {
            if (_group is not null) _group.AccountCalendars.CollectionChanged -= CalendarsChanged;
            base.OnDetachedFromWindow();
        }

        private void CalendarsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs args) => Post(Rebuild);

        private void Rebuild()
        {
            if (_group is not { } group || Context is not { } context) return;
            _name.Text = group.Account.Name;
            _address.Text = group.Account.Address;
            _calendars.RemoveAllViews();
            foreach (var calendar in group.AccountCalendars)
            {
                var row = new CalendarCheckRow(context);
                row.Bind(calendar, toggles: true);
                _calendars.AddView(row, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
            }
        }
    }
}
