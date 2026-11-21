using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.Linux.Common;
using Wino.Mail.Controls.Linux.Settings;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.ViewModels.Common;
using Wino.Mail.ViewModels.Common.Data;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Settings;

/// <summary>
/// Notifications (Windows NotificationSettingsPage): page status banners, instant push, snooze, quiet hours, the mail,
/// calendar and task sections, per-account overrides and reset.
/// </summary>
internal sealed class NotificationSettingsPage(NotificationSettingsPageViewModel viewModel, WinoViewServices services,
    IMailDialogService dialogs, IPictureStorageService pictures)
    : SettingsPageView<NotificationSettingsPageViewModel>(viewModel, services)
{
    private const uint SnoozeRefreshMilliseconds = 30_000;

    private WinoSettingsGroup? _accounts;
    private SettingsBinder? _accountRows;

    protected override void BuildPage()
    {
        var vm = ViewModel;
        var p = vm.PreferencesService;

        var snoozed = Page.AddBanner();
        Settings.Bind(vm, nameof(vm.SnoozeSummaryText), static s => s.SnoozeSummaryText, text => snoozed.SetTitle(text ?? string.Empty));
        Settings.Bind(vm, nameof(vm.IsSnoozed), static s => s.IsSnoozed, snoozed.SetRevealed);
        var quietTime = Page.AddBanner(Translator.NotificationSettings_DoNotDisturbOn);
        Settings.Bind(vm, nameof(vm.IsSystemQuietTimeActive), static s => s.IsSystemQuietTimeActive, quietTime.SetRevealed);

        if (vm.IsPushRelaySectionVisible) AddInstantPush(vm);

        AddGroup(null, Snooze(vm));

        // No sound rows: PL-06 adds them with the Linux notification sound provider (vm.HasSoundProvider).
        AddGroup(null, QuietHours(vm, p), Mail(vm, p), Calendar(vm, p), Tasks(vm, p));

        _accounts = Page.AddGroup(Translator.NotificationSettings_Accounts_Title, Translator.NotificationSettings_Accounts_Description);
        Settings.Collection(vm.Accounts, RebuildAccounts);

        var reset = WinoSettingsRows.Action(Translator.NotificationSettings_Reset_Title, Translator.NotificationSettings_Reset_Description, WinoIconGlyph.ArrowReset);
        WinoSettingsRows.Suffix(reset, Settings.Button(Translator.NotificationSettings_Reset_Title, () => Observe(ViewModel.ConfirmResetAsync(dialogs))));
        AddGroup(null, reset);
    }

    protected override Task NavigateAsync(NavigationMode mode, object? parameter)
    {
        ViewModel.OnNavigatedTo(mode, parameter);
        // Snooze expiry is evaluated lazily, so the page refreshes it while it is open (Windows uses a 30 s timer).
        Settings.Own(new RepeatingTimer(SnoozeRefreshMilliseconds, () =>
        {
            if (!IsReleased) ViewModel.RefreshSnoozeState();
        }));
        return Task.CompletedTask;
    }

    private void AddInstantPush(NotificationSettingsPageViewModel vm)
    {
        Adw.ActionRow push;
        if (vm.IsPushRelayToggleVisible)
        {
            push = Settings.Switch(vm, Translator.NotificationSettings_RealtimePush_Title, Translator.NotificationSettings_RealtimePush_Description, WinoIconGlyph.Flash,
                nameof(vm.IsPushRelayEnabled), static s => s.IsPushRelayEnabled, static (s, v) => s.IsPushRelayEnabled = v);
            Settings.Enabled(push, vm, nameof(vm.IsPushRelayToggleEnabled), static s => s.IsPushRelayToggleEnabled);
        }
        else
        {
            push = WinoSettingsRows.Action(Translator.NotificationSettings_RealtimePush_Title, Translator.NotificationSettings_RealtimePush_Description, WinoIconGlyph.Flash);
        }

        // The push relay is not connected on Linux yet, so the shared view model reports the relay as disconnected while
        // timer sync runs; its sign-in, reconnect and mailbox rows would ask for something that cannot work here.
        AddGroup(null,
            push,
            TextRow(nameof(vm.PushRelayProblemText), static s => s.PushRelayProblemText));
    }

    private Adw.PreferencesRow TextRow(string property, Func<NotificationSettingsPageViewModel, string?> read)
    {
        var caption = WinoSettingsRows.Caption(null);
        var row = WinoSettingsRows.Custom(caption);
        Settings.Bind(ViewModel, property, read, text =>
        {
            caption.SetText(text ?? string.Empty);
            row.SetVisible(!string.IsNullOrEmpty(text));
        });
        return row;
    }

    private Adw.ActionRow Snooze(NotificationSettingsPageViewModel vm)
    {
        var row = WinoSettingsRows.Action(Translator.NotificationSettings_Snooze_Title, Translator.NotificationSettings_Snooze_ActiveDescription, WinoIconGlyph.AlertOff);
        WinoSettingsRows.Suffix(row, Settings.ChoiceSuffix<NotificationSettingsPageViewModel, SnoozePresetOption>(vm, Translator.NotificationSettings_Snooze_Title,
            static s => s.SnoozePresets, static option => option.DisplayText,
            nameof(vm.SelectedSnoozePreset), static s => s.SelectedSnoozePreset, static (s, v) => s.StartSnoozeCommand.Execute(v.Value)));

        // Not a two-way binding: the switch runs the snooze commands, which change IsSnoozed and so the switch again.
        var toggle = WinoSettingsRows.Suffix(row, Gtk.Switch.New());
        WinoA11y.Label(toggle, Translator.NotificationSettings_SnoozeAll);
        Settings.Bind(vm, nameof(vm.IsSnoozed), static s => s.IsSnoozed, toggle.SetActive);
        Settings.Own(GtkSignals.OnNotify(toggle, "active", () =>
        {
            if (toggle.GetActive() == vm.IsSnoozed) return;
            if (toggle.GetActive()) vm.StartSnoozeCommand.Execute(vm.SelectedSnoozePreset?.Value ?? NotificationSnoozePreset.UntilTurnedBackOn);
            else vm.EndSnoozeCommand.Execute(null);
        }));
        return row;
    }

    private Adw.ExpanderRow QuietHours(NotificationSettingsPageViewModel vm, IPreferencesService p)
    {
        static bool On(IPreferencesService s) => s.AreQuietHoursEnabled;

        var hoursTitle = Settings.Enabled(WinoSettingsRows.Action(Translator.NotificationSettings_QuietHours_From_Title, Translator.NotificationSettings_QuietHours_From_Description),
            p, nameof(p.AreQuietHoursEnabled), On);
        // Four spin buttons beside the text squeeze the title to a word per line, so the times get a row of their own.
        var times = Adw.WrapBox.New();
        times.SetChildSpacing(8);
        times.SetLineSpacing(8);
        times.Append(Settings.TimeSuffix(p, Translator.NotificationSettings_QuietHours_From_Title, nameof(p.QuietHoursStart), static s => s.QuietHoursStart,
            (s, v) =>
            {
                if (s.QuietHoursStart != v) s.QuietHoursStart = v;
                vm.UpdateQuietHoursSummary();
            }));
        var to = Gtk.Label.New(Translator.NotificationSettings_QuietHours_To);
        to.SetValign(Gtk.Align.Center);
        times.Append(to);
        times.Append(Settings.TimeSuffix(p, Translator.NotificationSettings_QuietHours_To, nameof(p.QuietHoursEnd), static s => s.QuietHoursEnd,
            (s, v) =>
            {
                if (s.QuietHoursEnd != v) s.QuietHoursEnd = v;
                vm.UpdateQuietHoursSummary();
            }));
        var hours = Settings.Enabled(WinoSettingsRows.Custom(times, Translator.NotificationSettings_QuietHours_From_Title), p, nameof(p.AreQuietHoursEnabled), On);

        var daysTitle = Settings.Enabled(WinoSettingsRows.Action(Translator.NotificationSettings_QuietHours_Days_Title, Translator.NotificationSettings_QuietHours_Days_Description),
            p, nameof(p.AreQuietHoursEnabled), On);
        var days = Adw.WrapBox.New();
        days.SetChildSpacing(4);
        days.SetLineSpacing(4);
        var daysRow = Settings.Enabled(WinoSettingsRows.Custom(days, Translator.NotificationSettings_QuietHours_Days_Title), p, nameof(p.AreQuietHoursEnabled), On);
        var dayScope = new DayButtons(days, Settings);
        Settings.Collection(vm.QuietHoursDayToggles, () => dayScope.Rebuild(vm.QuietHoursDayToggles));

        var presenting = vm.IsSnoozeWhilePresentingAvailable
            ? Settings.Switch(p, Translator.NotificationSettings_QuietHours_Presenting_Title, Translator.NotificationSettings_QuietHours_Presenting_Description, WinoIconGlyph.None,
                nameof(p.SnoozeWhilePresenting), static s => s.SnoozeWhilePresenting, static (s, v) => s.SnoozeWhilePresenting = v)
            : null;

        var expander = WinoSettingsRows.Expander(Translator.NotificationSettings_QuietHours_Title, null, WinoIconGlyph.Clock, false, hoursTitle, hours, daysTitle, daysRow, presenting);
        WinoSettingsRows.Suffix(expander, Settings.SwitchSuffix(p, Translator.NotificationSettings_QuietHours_Title, nameof(p.AreQuietHoursEnabled), On,
            (s, v) =>
            {
                s.AreQuietHoursEnabled = v;
                vm.UpdateQuietHoursSummary();
            }));
        Settings.Subtitle(expander, vm, nameof(vm.QuietHoursSummary), static s => s.QuietHoursSummary);
        return expander;
    }

    private Adw.ExpanderRow Mail(NotificationSettingsPageViewModel vm, IPreferencesService p)
    {
        var expander = WinoSettingsRows.Expander(Translator.NotificationSettings_Mail_Title, null, WinoIconGlyph.Mail, false,
            Option(vm, Translator.NotificationSettings_Mail_Scope_Title, Translator.NotificationSettings_Mail_Scope_Description,
                static s => s.ScopeOptions, nameof(vm.SelectedMailScope), static s => s.SelectedMailScope, static (s, v) => s.SelectedMailScope = v),
            Option(vm, Translator.NotificationSettings_Mail_Content_Title, Translator.NotificationSettings_Mail_Content_Description,
                static s => s.ContentOptions, nameof(vm.SelectedMailContent), static s => s.SelectedMailContent, static (s, v) => s.SelectedMailContent = v),
            // Stored for notification buttons (PL-02); Linux notifications carry none yet.
            Option(vm, Translator.NotificationSettings_Mail_FirstAction_Title, Translator.NotificationSettings_Mail_FirstAction_Description,
                static s => s.MailActionOptions, nameof(vm.SelectedFirstAction), static s => s.SelectedFirstAction, static (s, v) => s.SelectedFirstAction = v),
            Option(vm, Translator.NotificationSettings_Mail_SecondAction_Title, Translator.NotificationSettings_Mail_SecondAction_Description,
                static s => s.MailActionOptions, nameof(vm.SelectedSecondAction), static s => s.SelectedSecondAction, static (s, v) => s.SelectedSecondAction = v));
        SectionSwitch(expander, vm, p, Translator.NotificationSettings_Mail_Title, nameof(p.AreNewMailNotificationsEnabled),
            static s => s.AreNewMailNotificationsEnabled, static (s, v) => s.AreNewMailNotificationsEnabled = v);
        Settings.Subtitle(expander, vm, nameof(vm.MailSummary), static s => s.MailSummary);
        return expander;
    }

    private Adw.ExpanderRow Calendar(NotificationSettingsPageViewModel vm, IPreferencesService p)
    {
        var expander = WinoSettingsRows.Expander(Translator.NotificationSettings_Calendar_Title, null, WinoIconGlyph.Calendar, false,
            Settings.Choice(vm, Translator.CalendarSettings_DefaultReminder_Header, Translator.CalendarSettings_DefaultReminder_Description, WinoIconGlyph.None,
                [.. vm.ReminderOptions], nameof(vm.SelectedReminderIndex), static s => s.SelectedReminderIndex, static (s, v) => s.SelectedReminderIndex = v),
            Settings.Choice(vm, Translator.CalendarSettings_DefaultSnoozeDuration_Header, Translator.CalendarSettings_DefaultSnoozeDuration_Description, WinoIconGlyph.None,
                [.. vm.CalendarSnoozeOptions], nameof(vm.SelectedCalendarSnoozeIndex), static s => s.SelectedCalendarSnoozeIndex, static (s, v) => s.SelectedCalendarSnoozeIndex = v));
        SectionSwitch(expander, vm, p, Translator.NotificationSettings_Calendar_Title, nameof(p.AreCalendarRemindersEnabled),
            static s => s.AreCalendarRemindersEnabled, static (s, v) => s.AreCalendarRemindersEnabled = v);
        Settings.Subtitle(expander, vm, nameof(vm.CalendarSummary), static s => s.CalendarSummary);
        return expander;
    }

    private Adw.ExpanderRow Tasks(NotificationSettingsPageViewModel vm, IPreferencesService p)
    {
        var expander = WinoSettingsRows.Expander(Translator.NotificationSettings_Tasks_Title, null, WinoIconGlyph.TaskList, false,
            Option(vm, Translator.NotificationSettings_Tasks_Timing_Title, Translator.NotificationSettings_Tasks_Timing_Description,
                static s => s.TaskReminderTimingOptions, nameof(vm.SelectedTaskReminderTiming), static s => s.SelectedTaskReminderTiming, static (s, v) => s.SelectedTaskReminderTiming = v),
            Settings.Choice(vm, Translator.NotificationSettings_Tasks_Snooze_Title, Translator.NotificationSettings_Tasks_Snooze_Description, WinoIconGlyph.None,
                [.. vm.TaskSnoozeOptions], nameof(vm.SelectedTaskSnoozeIndex), static s => s.SelectedTaskSnoozeIndex, static (s, v) => s.SelectedTaskSnoozeIndex = v));
        SectionSwitch(expander, vm, p, Translator.NotificationSettings_Tasks_Title, nameof(p.AreTaskRemindersEnabled),
            static s => s.AreTaskRemindersEnabled, static (s, v) => s.AreTaskRemindersEnabled = v);
        Settings.Subtitle(expander, vm, nameof(vm.TaskSummary), static s => s.TaskSummary);
        return expander;
    }

    private void SectionSwitch(Adw.ExpanderRow expander, NotificationSettingsPageViewModel vm, IPreferencesService p, string title, string property,
        Func<IPreferencesService, bool> read, Action<IPreferencesService, bool> write)
        => WinoSettingsRows.Suffix(expander, Settings.SwitchSuffix(p, title, property, read, (s, v) =>
        {
            write(s, v);
            vm.UpdateSectionSummaries();
        }));

    private Adw.ComboRow Option<TSource, TOption>(TSource source, string title, string description, Func<TSource, IReadOnlyList<TOption>?> options,
        string property, Func<TSource, TOption?> read, Action<TSource, TOption> write, SettingsBinder? binder = null)
        where TSource : System.ComponentModel.INotifyPropertyChanged where TOption : NotificationOptionBase
        => (binder ?? Settings).Choice(source, title, description, WinoIconGlyph.None, options, static option => option.DisplayText, property, read, write);

    private void RebuildAccounts()
    {
        if (_accounts is null) return;
        _accountRows?.Scope.Dispose();
        _accounts.Clear();
        var rows = _accountRows = Settings.Child();

        foreach (var account in ViewModel.Accounts.ToList())
        {
            static bool Enabled(AccountNotificationSettingsViewModel a) => a.AreNotificationsEnabled;
            static bool Overrides(AccountNotificationSettingsViewModel a) => a.AreOverrideFieldsEnabled;
            var name = account.Account.Name ?? string.Empty;

            var source = rows.Enabled(rows.Choice(account, Translator.NotificationSettings_Account_Source_Title, Translator.NotificationSettings_Account_Source_Description, WinoIconGlyph.None,
                [Translator.NotificationSettings_Account_UseDefaults, Translator.NotificationSettings_Account_Customise],
                nameof(account.HasCustomNotificationSettings), static a => a.HasCustomNotificationSettings ? 1 : 0, static (a, v) => a.HasCustomNotificationSettings = v == 1),
                account, nameof(account.AreNotificationsEnabled), Enabled);
            var newMail = rows.Enabled(rows.Switch(account, Translator.NotificationSettings_Account_NewMail_Title, Translator.NotificationSettings_Account_NewMail_Description, WinoIconGlyph.None,
                nameof(account.IsNewMailNotificationEnabled), static a => a.IsNewMailNotificationEnabled, static (a, v) => a.IsNewMailNotificationEnabled = v),
                account, nameof(account.AreOverrideFieldsEnabled), Overrides);
            var scope = rows.Enabled(Option(account, Translator.NotificationSettings_Mail_Scope_Title, Translator.NotificationSettings_Mail_Scope_Description,
                static a => a.ScopeOptions, nameof(account.SelectedScope), static a => a.SelectedScope, static (a, v) => a.SelectedScope = v, rows),
                account, nameof(account.AreOverrideFieldsEnabled), Overrides);
            var content = rows.Enabled(Option(account, Translator.NotificationSettings_Mail_Content_Title, Translator.NotificationSettings_Mail_Content_Description,
                static a => a.ContentOptions, nameof(account.SelectedContent), static a => a.SelectedContent, static (a, v) => a.SelectedContent = v, rows),
                account, nameof(account.AreOverrideFieldsEnabled), Overrides);
            var calendar = rows.Enabled(rows.Switch(account, Translator.NotificationSettings_Account_Calendar_Title, Translator.NotificationSettings_Account_Calendar_Description, WinoIconGlyph.None,
                nameof(account.AreCalendarRemindersEnabled), static a => a.AreCalendarRemindersEnabled, static (a, v) => a.AreCalendarRemindersEnabled = v),
                account, nameof(account.AreOverrideFieldsEnabled), Overrides);
            var quiet = rows.Enabled(Option(account, Translator.NotificationSettings_Account_QuietHours_Title, Translator.NotificationSettings_Account_QuietHours_Description,
                static a => a.QuietHoursOptions, nameof(account.SelectedQuietHoursStance), static a => a.SelectedQuietHoursStance, static (a, v) => a.SelectedQuietHoursStance = v, rows),
                account, nameof(account.AreNotificationsEnabled), Enabled);

            var expander = WinoSettingsRows.Expander(name, account.AccountAddress, WinoIconGlyph.None, false, source, newMail, scope, content, calendar, quiet);
            var icon = new WinoAccountIcon(28) { Account = MailAccountIconInfoFactory.Create(account.Account, pictures), AccessibleName = name };
            expander.AddPrefix(icon.Root);
            WinoSettingsRows.Suffix(expander, rows.SwitchSuffix(account, name, nameof(account.AreNotificationsEnabled), Enabled, static (a, v) => a.AreNotificationsEnabled = v));
            rows.Subtitle(expander, account, nameof(account.StatusText), a => string.IsNullOrEmpty(a.StatusText) ? a.AccountAddress : a.StatusText);
            _accounts.Add(expander);
        }
    }

    /// <summary>One toggle button per weekday; rebuilt when the ViewModel refills the days.</summary>
    private sealed class DayButtons(Adw.WrapBox box, SettingsBinder parent)
    {
        private SettingsBinder? _scope;

        public void Rebuild(IEnumerable<QuietHoursDayViewModel> days)
        {
            _scope?.Scope.Dispose();
            var scope = _scope = parent.Child();
            while (box.GetFirstChild() is { } child) box.Remove(child);
            foreach (var day in days.ToList())
            {
                var button = Gtk.ToggleButton.NewWithLabel(day.DisplayText);
                WinoA11y.Label(button, day.DisplayText);
                scope.Bind(day, nameof(day.IsSelected), static d => d.IsSelected, button.SetActive);
                // A multi-select: both directions write.
                scope.Own(GtkSignals.OnToggled(button, () =>
                {
                    if (day.IsSelected != button.GetActive()) day.IsSelected = button.GetActive();
                }));
                box.Append(button);
            }
        }
    }

    /// <summary>A repeating main-loop timer; dispose removes it exactly once.</summary>
    private sealed class RepeatingTimer : IDisposable
    {
        private uint _source;

        public RepeatingTimer(uint milliseconds, Action tick)
            => _source = GLib.Functions.TimeoutAdd(GLib.Constants.PRIORITY_DEFAULT, milliseconds, () =>
            {
                tick();
                return true;
            });

        public void Dispose()
        {
            if (_source == 0) return;
            GLib.Functions.SourceRemove(_source);
            _source = 0;
        }
    }
}
