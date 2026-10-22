using Android.Content;
using Android.Views;
using Android.Widget;
using CommunityToolkit.Mvvm.Messaging;
using Google.Android.Material.Button;
using Wino.Calendar.ViewModels;
using Wino.Calendar.ViewModels.Data;
using Wino.Messaging.Client.Calendar;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Calendar;
using Wino.Mail.Android.Infrastructure;
using Wino.Mail.Controls.Android.Calendar;
using Wino.Presentation.Android;

namespace Wino.Mail.Android.Views.Calendar;

/// <summary>
/// Medium and expanded widths (canvas board CalendarTablet; Windows CalendarPage and its title bar): the top app bar with
/// New event, the period header (previous, next, the range text, Today, Day · Week · Work week · Month) and the Windows
/// calendar surface. A tile tap opens the details in the shell's details pane; a tap on empty time opens the event
/// composer for that slot through the shared quick event range (the Windows quick event's "More details").
/// </summary>
public sealed partial class CalendarPageFragment
{
    private static readonly CalendarDisplayType[] SurfaceTypes = [CalendarDisplayType.Day, CalendarDisplayType.Week, CalendarDisplayType.WorkWeek, CalendarDisplayType.Month];
    private const int MenuNewEvent = 20;

    private WinoCalendarSurfaceView? _surface;
    private MaterialButtonToggleGroup? _types;
    private bool _applyingTypes;

    private void BuildSurface(View view, Context context, CalendarPageViewModel viewModel)
    {
        BuildSurfaceCore(view, context);
        _types = view.FindViewById<MaterialButtonToggleGroup>(Resource.Id.calendar_types);
        if (_surface is null) return;
        _refresh?.SetOnChildScrollUpCallback(new ScrollUpCallback(() => _surface?.CanScrollUp ?? false));

        if (_toolbar is not null)
        {
            _toolbar.Menu.Clear();
            var add = _toolbar.Menu.Add(0, MenuNewEvent, 0, Translator.CalendarEventCompose_NewEventButton);
            add?.SetIcon(WinoIcons.Drawable(context, WinoIconGlyph.Add, WinoTokens.Dp(context, Resource.Dimension.wino_m3_glyph), WinoColors.SecondaryText(context)));
            add?.SetShowAsAction(ShowAsAction.Always);
            _toolbar.MenuItemClick += (_, args) => { if (args.Item?.ItemId == MenuNewEvent) NewEvent(); };
        }

        ConfigureHeaderButton(view, context, Resource.Id.calendar_previous, WinoIconGlyph.ArrowLeft, Translator.Accessibility_PreviousCalendarPeriod, () => NavigatePeriods(-1));
        ConfigureHeaderButton(view, context, Resource.Id.calendar_next, WinoIconGlyph.ArrowRight, Translator.Accessibility_NextCalendarPeriod, () => NavigatePeriods(1));
        if (view.FindViewById<MaterialButton>(Resource.Id.calendar_today) is { } today)
        {
            today.Text = Translator.Today;
            today.Click += (_, _) => GoToday();
        }

        if (_types is not null)
        {
            var inflater = LayoutInflater.From(context);
            foreach (var type in SurfaceTypes)
            {
                if (inflater?.Inflate(Resource.Layout.mail_pivot_segment, _types, false) is not MaterialButton button) continue;
                button.Id = View.GenerateViewId();
                button.Text = type switch
                {
                    CalendarDisplayType.Day => Translator.CalendarTypeSelector_Day,
                    CalendarDisplayType.WorkWeek => Translator.CalendarTypeSelector_WorkWeek,
                    CalendarDisplayType.Month => Translator.CalendarTypeSelector_Month,
                    _ => Translator.CalendarTypeSelector_Week
                };
                button.Tag = (int)type;
                WinoMaterialAccent.TintM3Segment(button);
                _types.AddView(button);
            }
            var listener = new TypeListener(this);
            _types.AddOnButtonCheckedListener(listener);
            Bindings.Own(new ActionDisposable(() => _types?.RemoveOnButtonCheckedListener(listener)));
        }

    }

    /// <summary>The calendar surface every width shares: tiles, today, tile taps (details) and empty-slot taps.</summary>
    private void BuildSurfaceCore(View view, Context context)
    {
        _surface = view.FindViewById<WinoCalendarSurfaceView>(Resource.Id.calendar_surface);
        if (_surface is null) return;
        _surface.TileFactory = (item, date) => CalendarTiles.Map(context, item, date, ViewModel?.CurrentSettings);
        _surface.Today = () => TodayDate;
        _surface.ItemClicked += (_, args) => { if (args.Item is CalendarItemViewModel item) SelectItem(item); };
        _surface.SlotClicked += (_, args) =>
        {
            if (IsPhone) PhoneSlotClicked(args);
            else OpenComposerAt(args.Start, args.IsAllDay);
        };
        _surface.PeriodSettled += SurfacePeriodSettled;
        _surface.ViewportPeriodChanged += SurfaceViewportPeriodChanged;
    }

    /// <summary>Glides the rail by whole periods; before the surface shows a period the shell steps instead.</summary>
    private void NavigatePeriods(int direction)
    {
        if (_surface?.NavigatePeriods(direction) == true) return;
        if (direction < 0) GoPrevious();
        else GoNext();
    }

    /// <summary>
    /// The rail settled on a period the view model has not loaded; the view model answers through CurrentVisibleRange,
    /// which confirms the position without moving the rail again.
    /// </summary>
    private void SurfacePeriodSettled(object? sender, CalendarPeriodEventArgs args)
        => WeakReferenceMessenger.Default.Send(new LoadCalendarMessage(new CalendarDisplayRequest(args.DisplayType, args.AnchorDate)));

    /// <summary>While the rail moves the title names the period nearest to the viewport; the view model writes the same text once it loads.</summary>
    private void SurfaceViewportPeriodChanged(object? sender, CalendarPeriodEventArgs args)
    {
        if (ViewModel is { } viewModel) RangeTextChanged(viewModel.FormatRangeText(args.DisplayType, args.AnchorDate));
    }

    private void ConfigureHeaderButton(View view, Context context, int id, WinoIconGlyph glyph, string description, Action action)
    {
        if (view.FindViewById<WinoIconView>(id) is not { } button) return;
        button.Icon = glyph;
        button.GlyphSize = WinoTokens.Dp(context, Resource.Dimension.wino_glyph_m);
        button.ContentDescription = description;
        button.Clickable = true;
        button.Click += (_, _) => action();
    }

    /// <summary>The Windows quick event's "More details": the slot's range goes to the full composer.</summary>
    private void OpenComposerAt(DateTime start, bool isAllDay)
    {
        if (ViewModel is not { } viewModel || !viewModel.Readiness.IsReady) return;
        var end = isAllDay ? start.Date.AddDays(1) : start.AddMinutes(30);
        viewModel.SelectQuickEventRange(start, end, isAllDay);
        if (viewModel.GoToEventComposePageCommand.CanExecute(null)) viewModel.GoToEventComposePageCommand.Execute(null);
    }

    private void SurfaceRangeChanged(VisibleDateRange? range)
    {
        if (_surface is null) return;
        _surface.VisibleRange = range;
    }

    private void SurfaceSettingsChanged(CalendarSettings? settings)
    {
        if (_surface is not null) _surface.Settings = settings;
    }

    private void SurfaceRangeTextChanged(string? text)
    {
        // The range leads the period header (the search bar took the app bar title's place).
        if (View?.FindViewById<TextView>(Resource.Id.calendar_range) is { } range) range.Text = string.IsNullOrWhiteSpace(text) ? Translator.KeyboardShortcuts_ModeCalendar : text;
    }

    private void SurfaceItemsChanged()
    {
        if (_surface is null || ViewModel is not { } viewModel) return;
        _surface.Items = viewModel.CalendarItems;
    }

    private void SurfaceSelectionChanged() => _surface?.RefreshTiles();

    private void SurfaceReadinessChanged(bool blocked)
    {
        if (_types is not null) _types.Enabled = !blocked;
    }

    private void SurfaceDisplayTypeChanged(CalendarDisplayType type)
    {
        if (_types is null) return;
        _applyingTypes = true;
        try
        {
            for (int index = 0; index < _types.ChildCount; index++)
            {
                if (_types.GetChildAt(index) is not MaterialButton button || button.Tag is not Java.Lang.Integer tag) continue;
                bool selected = tag.IntValue() == (int)type;
                if (selected) _types.Check(button.Id);
                button.Icon = selected && _types.Context is { } context ? AndroidX.Core.Content.ContextCompat.GetDrawable(context, Resource.Drawable.wino_ic_check) : null;
            }
        }
        finally { _applyingTypes = false; }
    }

    private void TypeChecked(MaterialButtonToggleGroup? group, int checkedId, bool isChecked)
    {
        if (_applyingTypes || !isChecked || group?.FindViewById<MaterialButton>(checkedId)?.Tag is not Java.Lang.Integer tag) return;
        RequestDisplayType((CalendarDisplayType)tag.IntValue());
    }

    private sealed class TypeListener(CalendarPageFragment owner) : Java.Lang.Object, MaterialButtonToggleGroup.IOnButtonCheckedListener
    {
        public void OnButtonChecked(MaterialButtonToggleGroup? group, int checkedId, bool isChecked) => owner.TypeChecked(group, checkedId, isChecked);
    }

    private sealed class ScrollUpCallback(Func<bool> canScrollUp) : Java.Lang.Object, AndroidX.SwipeRefreshLayout.Widget.SwipeRefreshLayout.IOnChildScrollUpCallback
    {
        public bool CanChildScrollUp(AndroidX.SwipeRefreshLayout.Widget.SwipeRefreshLayout parent, View? child) => canScrollUp();
    }
}
