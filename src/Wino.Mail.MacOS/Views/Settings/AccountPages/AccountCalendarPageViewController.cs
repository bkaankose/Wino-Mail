using System.Linq;
using AppKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Controls.AppKit.Common;
using Wino.Mail.Controls.AppKit.Settings;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.AccountSettings;
using Wino.Mail.ViewModels.Calendar;
using Wino.Presentation.AppKit;
using Wino.Presentation.Bindings;

namespace Wino.Mail.MacOS.Views.Settings.AccountPages;

/// <summary>The Calendar app of an account (Windows AccountCalendarSettingsPage): the primary calendar and each calendar's settings.</summary>
public sealed class AccountCalendarPageViewController(AccountCalendarSettingsPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger)
    : AccountAppPageViewController<AccountCalendarSettingsPageViewModel>(viewModel, dispatcher, logger)
{
    private readonly WinoSettingsGroup _calendars = new();
    private BindingScope? _rowScope;

    protected override void BuildAppSettings()
    {
        var vm = ViewModel;
        var primary = Card(Translator.CalendarAccountSettings_PrimaryCalendar, Translator.CalendarAccountSettings_PrimaryCalendarDescription, WinoIconGlyph.Star,
            Bind.PopUp<AccountCalendarSettingsPageViewModel, AccountCalendar>(vm, s => s.AccountCalendars.ToList(), calendar => calendar.Name,
                nameof(vm.SelectedPrimaryCalendar), s => s.SelectedPrimaryCalendar, (s, v) => s.SelectedPrimaryCalendar = v,
                width: 180, itemsCollection: vm.AccountCalendars));
        Bind.Visible(primary, vm, nameof(vm.HasCalendars), s => s.HasCalendars);

        var empty = Caption(Translator.AccountApps_NoCalendars);
        Bind.Visible(empty, vm, nameof(vm.HasCalendars), s => !s.HasCalendars);
        AddGroup(Translator.AccountApps_CalendarsHeader, primary, empty);
        Add(_calendars);
        Bind.Collection(vm.CalendarItems, RebuildCalendars);

    }

    private void RebuildCalendars()
    {
        _rowScope?.Dispose();
        _rowScope = Bindings.Own(new BindingScope());
        var rows = Bind.Child(_rowScope);
        _calendars.Clear();
        foreach (var item in ViewModel.CalendarItems.ToList())
        {
            var dot = new WinoSurfaceView { CornerRadius = 5, Fill = WinoStyle.FromHexString(item.BackgroundColorHex) ?? WinoStyle.Accent };
            WinoLayout.Size(dot, 10, 10);
            var expander = new WinoSettingsExpander(item.Name, item.TimeZone, WinoIconGlyph.None);
            expander.HeaderCard.LeadingView = dot;
            if (item.IsSynchronizationConfigurable)
                expander.Add(new WinoSettingsCard(Translator.CalendarAccountSettings_SyncEnabled, Translator.CalendarAccountSettings_SyncEnabledDescription, WinoIconGlyph.Sync,
                    rows.Switch(item, nameof(item.IsSynchronizationEnabled), s => s.IsSynchronizationEnabled, (s, v) => s.IsSynchronizationEnabled = v, Translator.CalendarAccountSettings_SyncEnabled)));
            expander.Add(new WinoSettingsCard(Translator.CalendarAccountSettings_DefaultShowAs, Translator.CalendarAccountSettings_DefaultShowAsDescription, WinoIconGlyph.Calendar,
                rows.PopUp<AccountCalendarSettingsItemViewModel, AccountCalendarShowAsOption>(item, s => s.ShowAsOptions.ToList(), option => option.DisplayText,
                    nameof(item.SelectedShowAsOption), s => s.SelectedShowAsOption, (s, v) => s.SelectedShowAsOption = v, width: 150)));
            expander.Add(new WinoSettingsCard(Translator.CalendarAccountSettings_DefaultReminder, Translator.CalendarAccountSettings_DefaultReminderDescription, WinoIconGlyph.Alert,
                rows.PopUp<AccountCalendarSettingsItemViewModel, CalendarDefaultReminderOption>(item, s => s.DefaultReminderOptions, option => option.DisplayText,
                    nameof(item.SelectedDefaultReminderOption), s => s.SelectedDefaultReminderOption, (s, v) => s.SelectedDefaultReminderOption = v, width: 150)));

            var swatches = new WinoColorSwatchPicker { Colors = item.AvailableColors.Select(color => color.Hex).ToList() };
            swatches.WidthAnchor.ConstraintEqualTo(260).Active = true;
            rows.Bind(item, nameof(item.SelectedColor), s => s.SelectedColor, color =>
            {
                swatches.SelectedHex = color?.Hex ?? item.BackgroundColorHex;
                dot.Fill = WinoStyle.FromHexString(color?.Hex ?? item.BackgroundColorHex) ?? WinoStyle.Accent;
            });
            System.EventHandler<string> picked = (_, hex) =>
            {
                var match = item.AvailableColors.FirstOrDefault(color => string.Equals(color.Hex, hex, System.StringComparison.OrdinalIgnoreCase));
                if (match is not null) item.SetBackgroundColor(match);
            };
            swatches.SelectionChanged += picked;
            rows.Scope.Own(new SettingsDisposable(() => swatches.SelectionChanged -= picked));
            expander.Add(new WinoSettingsCard(Translator.CalendarAccountSettings_AccountColor, Translator.CalendarAccountSettings_AccountColorDescription, WinoIconGlyph.Color, swatches));
            _calendars.Add(expander);
        }
        _calendars.Hidden = _calendars.RowCount == 0;
    }
}
