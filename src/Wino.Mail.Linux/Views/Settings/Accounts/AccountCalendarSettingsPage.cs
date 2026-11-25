using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Enums;
using Wino.Mail.ViewModels.Calendar;
using Wino.Mail.Controls.Linux.Settings;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.AccountSettings;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Style;

namespace Wino.Mail.Linux.Views.Settings.Accounts;

/// <summary>
/// The Calendar app of an account (Windows AccountCalendarSettingsPage, macOS AccountCalendarPageViewController): the
/// primary calendar, then one expander per calendar with its synchronization, default show-as, default reminder and colour.
/// Each change is stored at once through the ViewModel's update methods, as on Windows.
/// </summary>
internal sealed class AccountCalendarSettingsPage(AccountCalendarSettingsPageViewModel viewModel, WinoViewServices services)
    : AccountAppPage<AccountCalendarSettingsPageViewModel>(viewModel, services)
{
    private readonly List<WinoColorSwatches> _swatches = [];
    private readonly List<(AccountCalendarSettingsItemViewModel Item, Adw.ExpanderRow Row)> _expanders = [];
    private WinoSettingsGroup? _calendars;
    private SettingsBinder? _rows;

    protected override void BuildAppSettings()
    {
        var vm = ViewModel;
        var primary = Settings.Choice<AccountCalendarSettingsPageViewModel, AccountCalendar>(vm, Translator.CalendarAccountSettings_PrimaryCalendar,
            Translator.CalendarAccountSettings_PrimaryCalendarDescription, WinoIconGlyph.Star,
            static s => s.AccountCalendars, static calendar => calendar.Name ?? string.Empty,
            nameof(vm.SelectedPrimaryCalendar), static s => s.SelectedPrimaryCalendar, static (s, v) => s.SelectedPrimaryCalendar = v,
            itemsCollection: vm.AccountCalendars);
        Settings.Visible(primary, vm, nameof(vm.HasCalendars), static s => s.HasCalendars);
        var empty = WinoSettingsRows.Action(Translator.AccountApps_NoCalendars, null, WinoIconGlyph.Calendar);
        Settings.Visible(empty, vm, nameof(vm.HasCalendars), static s => !s.HasCalendars);
        Page.AddGroup(Translator.AccountApps_CalendarsHeader, null, primary, empty);
        _calendars = Page.AddGroup();
        Settings.Collection(vm.CalendarItems, RebuildCalendars);
    }

    protected override Task DeactivateAsync()
    {
        foreach (var swatches in _swatches) swatches.Release();
        _swatches.Clear();
        return base.DeactivateAsync();
    }

    private void RebuildCalendars()
    {
        if (_calendars is null || IsReleased) return;
        _rows?.Scope.Dispose();
        var rows = _rows = Settings.Child();
        foreach (var swatches in _swatches) swatches.Release();
        _swatches.Clear();
        _expanders.Clear();
        _calendars.Clear();
        var vm = ViewModel;

        foreach (var item in vm.CalendarItems.ToList())
        {
            var expander = WinoSettingsRows.Expander(item.Name ?? string.Empty, item.TimeZone);
            var dot = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
            dot.AddCssClass("wino-color-dot");
            dot.SetValign(Gtk.Align.Center);
            string? dotClass = null;
            expander.AddPrefix(dot);

            if (item.IsSynchronizationConfigurable)
            {
                expander.AddRow(rows.Switch(item, Translator.CalendarAccountSettings_SyncEnabled, Translator.CalendarAccountSettings_SyncEnabledDescription, WinoIconGlyph.Sync,
                    nameof(item.IsSynchronizationEnabled), static s => s.IsSynchronizationEnabled,
                    (s, value) => Observe(vm.UpdateCalendarSynchronizationAsync(s, value))));
            }
            expander.AddRow(rows.Choice<AccountCalendarSettingsItemViewModel, AccountCalendarShowAsOption>(item, Translator.CalendarAccountSettings_DefaultShowAs,
                Translator.CalendarAccountSettings_DefaultShowAsDescription, WinoIconGlyph.Calendar,
                static s => s.ShowAsOptions, static option => option.DisplayText ?? string.Empty,
                nameof(item.SelectedShowAsOption), static s => s.SelectedShowAsOption,
                (s, value) => Observe(vm.UpdateCalendarDefaultShowAsAsync(s, value))));
            expander.AddRow(rows.Choice<AccountCalendarSettingsItemViewModel, CalendarDefaultReminderOption>(item, Translator.CalendarAccountSettings_DefaultReminder,
                Translator.CalendarAccountSettings_DefaultReminderDescription, WinoIconGlyph.Alert,
                static s => s.DefaultReminderOptions, static option => option.DisplayText ?? string.Empty,
                nameof(item.SelectedDefaultReminderOption), static s => s.SelectedDefaultReminderOption,
                (s, value) => Observe(vm.UpdateCalendarDefaultReminderAsync(s, value))));

            var swatches = new WinoColorSwatches(Translator.CalendarAccountSettings_AccountColor)
            {
                Colors = item.AvailableColors.Select(static color => color.Hex).ToList()
            };
            _swatches.Add(swatches);
            rows.Bind(item, nameof(item.SelectedColor), static s => s.SelectedColor, color =>
            {
                var hex = color?.Hex ?? item.BackgroundColorHex;
                swatches.SelectedHex = hex;
                var next = WinoTint.FillClassFor(hex, null);
                if (dotClass is not null) dot.RemoveCssClass(dotClass);
                if (next is not null) dot.AddCssClass(next);
                dotClass = next;
            });
            swatches.SelectionChanged += hex =>
            {
                var match = item.AvailableColors.FirstOrDefault(color => string.Equals(color.Hex, hex, StringComparison.OrdinalIgnoreCase));
                if (match is not null) Observe(vm.UpdateCalendarColorAsync(item, match));
            };
            expander.AddRow(WinoSettingsRows.Action(Translator.CalendarAccountSettings_AccountColor, Translator.CalendarAccountSettings_AccountColorDescription, WinoIconGlyph.Color));
            expander.AddRow(WinoSettingsRows.Custom(swatches.Root, Translator.CalendarAccountSettings_AccountColor));
            WinoA11y.Rename(expander, $"{item.Name}, {item.TimeZone}");
            _calendars.Add(expander);
            _expanders.Add((item, expander));
        }
    }

#if DEBUG
    internal bool DebugSet(string name, string field, string value)
    {
        var vm = ViewModel;
        var item = vm.CalendarItems.FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
        if (item is null) return false;
        switch (field)
        {
            case "sync":
                Observe(vm.UpdateCalendarSynchronizationAsync(item, value == "on"));
                return true;
            case "showas":
                var showAs = item.ShowAsOptions.FirstOrDefault(option => string.Equals(option.ShowAs.ToString(), value, StringComparison.OrdinalIgnoreCase));
                if (showAs is null) return false;
                Observe(vm.UpdateCalendarDefaultShowAsAsync(item, showAs));
                return true;
            case "reminder":
                var reminder = item.DefaultReminderOptions.FirstOrDefault(option => (option.Seconds?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "default") == value);
                if (reminder is null) return false;
                Observe(vm.UpdateCalendarDefaultReminderAsync(item, reminder));
                return true;
            case "color":
                var color = item.AvailableColors.FirstOrDefault(candidate => string.Equals(candidate.Hex, value, StringComparison.OrdinalIgnoreCase));
                if (color is null) return false;
                Observe(vm.UpdateCalendarColorAsync(item, color));
                return true;
            default:
                return false;
        }
    }

    internal override string DebugDescribeContent()
    {
        var vm = ViewModel;
        return $"primary={vm.SelectedPrimaryCalendar?.Name} calendars=" + string.Join(" ; ", _expanders.Select(static entry =>
            $"{entry.Item.Name}|sync={entry.Item.IsSynchronizationEnabled}|showas={entry.Item.SelectedShowAsOption?.ShowAs}|reminder={entry.Item.SelectedDefaultReminderOption?.Seconds}|color={entry.Item.SelectedColor?.Hex ?? entry.Item.BackgroundColorHex}")) + "\n";
    }
#endif
}
