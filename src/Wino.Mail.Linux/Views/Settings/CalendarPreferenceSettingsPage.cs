using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.Linux.Settings;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.ViewModels.Calendar;
using Wino.Mail.ViewModels.Calendar.Data;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Settings;

/// <summary>
/// Calendar preferences (Windows CalendarPreferenceSettingsPage): overlapping events with the sample, sync interval,
/// account grouping with the startup account, and where new events go.
/// </summary>
internal sealed class CalendarPreferenceSettingsPage(CalendarPreferenceSettingsPageViewModel viewModel, WinoViewServices services)
    : SettingsPageView<CalendarPreferenceSettingsPageViewModel>(viewModel, services)
{
    protected override void BuildPage()
    {
        var vm = ViewModel;

        var mode = Settings.Choice<CalendarPreferenceSettingsPageViewModel, CalendarEventDisplayModeOption>(vm, Translator.CalendarSettings_OverlappingEvents_Header, null, WinoIconGlyph.None,
            static s => s.EventDisplayModeOptions, static o => o.DisplayName,
            nameof(vm.SelectedEventDisplayModeOption), static s => s.SelectedEventDisplayModeOption, static (s, v) => s.SelectedEventDisplayModeOption = v);
        Settings.Subtitle(mode, vm, nameof(vm.SelectedEventDisplayModeOption), static s => s.SelectedEventDisplayModeOption?.Description);
        var overlap = WinoSettingsRows.Expander(Translator.CalendarSettings_Overlap_Header, Translator.CalendarSettings_Overlap_Description, WinoIconGlyph.CalendarWeek, true, mode);

        var sync = vm.IsSyncIntervalAvailable
            ? Settings.Number(vm, Translator.CalendarSettings_SyncInterval_Title, Translator.CalendarSettings_SyncInterval_Description, WinoIconGlyph.Sync,
                nameof(vm.CalendarSyncIntervalMinutes), static s => s.CalendarSyncIntervalMinutes, static (s, v) => s.CalendarSyncIntervalMinutes = v, 1, 1440)
            : null;

        var startup = Settings.Choice<CalendarPreferenceSettingsPageViewModel, MailAccount>(vm, Translator.CalendarSettings_StartupAccount_Header, Translator.CalendarSettings_StartupAccount_Description, WinoIconGlyph.None,
            static s => s.CalendarStartupAccounts, static account => string.IsNullOrWhiteSpace(account.Name) ? account.DisplayAddress ?? string.Empty : $"{account.Name} — {account.DisplayAddress}",
            nameof(vm.SelectedCalendarStartupAccount), static s => s.SelectedCalendarStartupAccount, static (s, v) => s.SelectedCalendarStartupAccount = v,
            itemsCollection: vm.CalendarStartupAccounts);
        Settings.Visible(startup, vm, nameof(vm.ShouldShowCalendarStartupAccount), static s => s.ShouldShowCalendarStartupAccount);

        var behavior = Settings.Choice<CalendarPreferenceSettingsPageViewModel, CalendarNewEventBehaviorOption>(vm, Translator.CalendarSettings_NewEventBehavior_Header, Translator.CalendarSettings_NewEventBehavior_Description, WinoIconGlyph.Calendar,
            static s => s.NewEventBehaviorOptions, static o => o.DisplayText,
            nameof(vm.SelectedNewEventBehaviorOption), static s => s.SelectedNewEventBehaviorOption, static (s, v) => s.SelectedNewEventBehaviorOption = v,
            itemsCollection: vm.NewEventBehaviorOptions);
        var calendar = Settings.ChoiceSuffix<CalendarPreferenceSettingsPageViewModel, AccountCalendarViewModel>(vm, Translator.CalendarSettings_NewEventBehavior_Header,
            static s => s.AvailableNewEventCalendars, static item => $"{item.Name} — {item.Account?.DisplayAddress}",
            nameof(vm.SelectedNewEventCalendar), static s => s.SelectedNewEventCalendar, static (s, v) => s.SelectedNewEventCalendar = v,
            itemsCollection: vm.AvailableNewEventCalendars);
        calendar.SetHalign(Gtk.Align.End);
        calendar.SetHexpand(true);
        var calendarRow = Settings.Visible(WinoSettingsRows.Custom(calendar), vm, nameof(vm.ShouldShowSpecificNewEventCalendar), static s => s.ShouldShowSpecificNewEventCalendar);

        AddGroup(null,
            overlap,
            WinoSettingsRows.Action(Translator.CalendarSettings_Overlap_Preview, Translator.CalendarSettings_Overlap_PreviewDescription),
            OverlapPreview(),
            sync,
            Settings.Switch(vm, Translator.CalendarSettings_AccountGrouping_Header, Translator.CalendarSettings_AccountGrouping_Description, WinoIconGlyph.Folder,
                nameof(vm.IsCalendarAccountsGrouped), static s => s.IsCalendarAccountsGrouped, static (s, v) => s.IsCalendarAccountsGrouped = v),
            startup,
            behavior,
            calendarRow);
    }

    protected override async Task NavigateAsync(NavigationMode mode, object? parameter)
    {
        await base.NavigateAsync(mode, parameter);
        try { await ViewModel.InitializationTask; }
        catch (Exception exception) { ReportError(exception); }
    }

    /// <summary>
    /// The Windows 410x196 overlap sample: a 44 px hour gutter, hour lines and the placed sample events. A narrow window
    /// scrolls it sideways instead of clipping it.
    /// </summary>
    private Adw.PreferencesRow OverlapPreview()
    {
        const int Gutter = 44;
        var canvas = Gtk.Fixed.New();
        canvas.SetSizeRequest(410, 196);
        WinoA11y.Label(canvas, Translator.CalendarSettings_Overlap_Preview);
        string[] hours = ["09:00", "10:00", "11:00"];
        for (var index = 0; index < 4; index++)
        {
            var y = 4 + index * 60;
            var line = Gtk.Separator.New(Gtk.Orientation.Horizontal);
            line.SetSizeRequest(410 - Gutter, 1);
            canvas.Put(line, Gutter, y);
            if (index < hours.Length)
            {
                var label = Gtk.Label.New(hours[index]);
                label.AddCssClass("caption");
                label.AddCssClass("dim-label");
                canvas.Put(label, 0, y);
            }
        }

        var events = new List<Gtk.Widget>();
        Settings.Bind(ViewModel, nameof(ViewModel.OverlapPreviewItems), static s => s.OverlapPreviewItems, items =>
        {
            foreach (var widget in events) canvas.Remove(widget);
            events.Clear();
            foreach (var item in items ?? [])
            {
                var block = Gtk.Label.New(item.Title);
                block.AddCssClass("caption");
                block.AddCssClass(Wino.Presentation.Linux.Style.WinoCssClass.Chip);
                block.SetXalign(0);
                block.SetYalign(0);
                block.SetEllipsize(Pango.EllipsizeMode.End);
                block.SetSizeRequest(Math.Max(1, (int)Math.Round(item.Width)), Math.Max(1, (int)Math.Round(item.Height)));
                canvas.Put(block, Gutter + item.Left, 4 + item.Top);
                events.Add(block);
            }
        });

        var scroller = Gtk.ScrolledWindow.New();
        scroller.SetPolicy(Gtk.PolicyType.Automatic, Gtk.PolicyType.Never);
        scroller.SetPropagateNaturalHeight(true);
        scroller.SetChild(canvas);
        return WinoSettingsRows.Custom(scroller);
    }
}
