using AppKit;
using Foundation;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Notifications;
using Wino.Core.ViewModels;
using Wino.Core.ViewModels.Data;
using Wino.Mail.Controls.AppKit.Common;
using Wino.Mail.Controls.AppKit.Settings;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Mail.ViewModels.Data;
using Wino.Platform.MacOS.Services;
using Wino.Presentation.AppKit;
using Wino.Presentation.Bindings;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>
/// Notifications: status bars, instant push (the sync method), snooze, quiet hours, mail, calendar and task sections, per-account overrides and
/// reset (Windows NotificationSettingsPage). Sounds are macOS alert sounds stored in Mac-only keys
/// (the ViewModel's sound provider), each with a Play preview; Windows' sound values are never written.
/// "Snooze while presenting" uses <see cref="MacUserPresenceStateProvider"/>'s full-display window check.
/// </summary>
public sealed class NotificationSettingsPageViewController(NotificationSettingsPageViewModel viewModel, IMailDialogService dialogs,
    IPictureStorageService pictures, IDispatcher dispatcher, IWinoLogger logger)
    : SettingsPageViewController<NotificationSettingsPageViewModel>(viewModel, dispatcher, logger)
{
    private readonly NSStackView _accounts = WinoLayout.VStack(8);
    private readonly List<Action> _soundRefreshers = [];
    private readonly List<Action> _accountSoundRefreshers = [];
    private BindingScope? _accountScope;
    private NSTimer? _snoozeTimer;

    protected override void BuildPage()
    {
        var vm = ViewModel;
        var p = vm.PreferencesService;

        // Page status, above everything: held notifications, then macOS Focus (which never says when it ends).
        var snoozed = InfoBar(WinoInfoBarSeverity.Informational, null, null);
        Bind.Bind(vm, nameof(vm.SnoozeSummaryText), s => s.SnoozeSummaryText, text => snoozed.Message = text);
        Bind.Visible(snoozed, vm, nameof(vm.IsSnoozed), s => s.IsSnoozed);
        Add(snoozed);

        var quietTime = InfoBar(WinoInfoBarSeverity.Informational, null, Translator.NotificationSettings_DoNotDisturbOn);
        Bind.Visible(quietTime, vm, nameof(vm.IsSystemQuietTimeActive), s => s.IsSystemQuietTimeActive);
        Add(quietTime);

        if (vm.IsPushRelaySectionVisible)
            AddInstantPush();

        // Snooze: a regular card. While notifications are held, the status shows at the top of the page instead.
        var snoozeSwitch = new WinoLabeledSwitch();
        WinoAccessibility.Label(snoozeSwitch.Switch, Translator.NotificationSettings_SnoozeAll);
        Bind.Bind(vm, nameof(vm.IsSnoozed), s => s.IsSnoozed, value => snoozeSwitch.IsOn = value);
        Bind.OnActivated(snoozeSwitch.Switch, () =>
        {
            if (snoozeSwitch.IsOn == vm.IsSnoozed) return;
            if (snoozeSwitch.IsOn) vm.StartSnoozeCommand.Execute(vm.SelectedSnoozePreset?.Value ?? NotificationSnoozePreset.UntilTurnedBackOn);
            else vm.EndSnoozeCommand.Execute(null);
        });
        var presets = Bind.PopUp<NotificationSettingsPageViewModel, SnoozePresetOption>(vm, s => s.SnoozePresets, option => option.DisplayText,
            nameof(vm.SelectedSnoozePreset), s => s.SelectedSnoozePreset, (s, v) => s.StartSnoozeCommand.Execute(v.Value), width: 170);
        // The duration sits under the text: beside the switch it leaves the text column too narrow.
        var snooze = Card(Translator.NotificationSettings_Snooze_Title, Translator.NotificationSettings_Snooze_ActiveDescription, WinoIconGlyph.AlertOff, snoozeSwitch);
        snooze.BottomContent = WinoLayout.VStack(0, presets);
        AddGroup(null, snooze);

        // Quiet hours.
        bool QuietOn(IPreferencesService s) => s.AreQuietHoursEnabled;
        var days = Row();
        foreach (var day in vm.QuietHoursDayToggles)
        {
            var toggle = new NSButton { Title = day.DisplayText, BezelStyle = NSBezelStyle.Rounded, ControlSize = NSControlSize.Small, TranslatesAutoresizingMaskIntoConstraints = false };
            toggle.SetButtonType(NSButtonType.PushOnPushOff);
            Bind.Bind(day, nameof(day.IsSelected), d => d.IsSelected, value => toggle.State = value ? NSCellStateValue.On : NSCellStateValue.Off);
            Bind.OnActivated(toggle, () => day.IsSelected = toggle.State == NSCellStateValue.On);
            days.AddArrangedSubview(toggle);
        }
        days.Spacing = 4;
        var hours = Row(TimePicker(p, nameof(p.QuietHoursStart), s => s.QuietHoursStart, (s, v) => s.QuietHoursStart = v, Translator.NotificationSettings_QuietHours_From_Title),
            Caption(Translator.NotificationSettings_QuietHours_To),
            TimePicker(p, nameof(p.QuietHoursEnd), s => s.QuietHoursEnd, (s, v) => s.QuietHoursEnd = v, Translator.NotificationSettings_QuietHours_To));
        var quiet = Expander(Translator.NotificationSettings_QuietHours_Title, null, WinoIconGlyph.Clock,
            Bind.Switch(p, nameof(p.AreQuietHoursEnabled), QuietOn, (s, v) => { s.AreQuietHoursEnabled = v; vm.UpdateQuietHoursSummary(); }, Translator.NotificationSettings_QuietHours_Title),
            Bind.Enabled(Card(Translator.NotificationSettings_QuietHours_From_Title, Translator.NotificationSettings_QuietHours_From_Description, WinoIconGlyph.None, hours), p, nameof(p.AreQuietHoursEnabled), QuietOn),
            Bind.Enabled(Card(Translator.NotificationSettings_QuietHours_Days_Title, Translator.NotificationSettings_QuietHours_Days_Description, WinoIconGlyph.None, days), p, nameof(p.AreQuietHoursEnabled), QuietOn),
            // Presenting is detected by MacUserPresenceStateProvider (a full-display window in front).
            vm.IsSnoozeWhilePresentingAvailable
                ? Card(Translator.NotificationSettings_QuietHours_Presenting_Title, Translator.NotificationSettings_QuietHours_Presenting_Description, WinoIconGlyph.None,
                    Bind.Switch(p, nameof(p.SnoozeWhilePresenting), s => s.SnoozeWhilePresenting, (s, v) => s.SnoozeWhilePresenting = v, Translator.NotificationSettings_QuietHours_Presenting_Title))
                : null);
        Bind.Bind(vm, nameof(vm.QuietHoursSummary), s => s.QuietHoursSummary, text => quiet.HeaderCard.Description = text);

        // Mail.
        var mail = Expander(Translator.NotificationSettings_Mail_Title, null, WinoIconGlyph.Mail,
            Bind.Switch(p, nameof(p.AreNewMailNotificationsEnabled), s => s.AreNewMailNotificationsEnabled, (s, v) => { s.AreNewMailNotificationsEnabled = v; vm.UpdateSectionSummaries(); }, Translator.NotificationSettings_Mail_Title),
            Card(Translator.NotificationSettings_Mail_Scope_Title, Translator.NotificationSettings_Mail_Scope_Description, WinoIconGlyph.None,
                OptionPopUp(vm, s => s.ScopeOptions, nameof(vm.SelectedMailScope), s => s.SelectedMailScope, (s, v) => s.SelectedMailScope = v)),
            Card(Translator.NotificationSettings_Mail_Content_Title, Translator.NotificationSettings_Mail_Content_Description, WinoIconGlyph.None,
                OptionPopUp(vm, s => s.ContentOptions, nameof(vm.SelectedMailContent), s => s.SelectedMailContent, (s, v) => s.SelectedMailContent = v)),
            Card(Translator.NotificationSettings_Mail_FirstAction_Title, Translator.NotificationSettings_Mail_FirstAction_Description, WinoIconGlyph.None,
                OptionPopUp(vm, s => s.MailActionOptions, nameof(vm.SelectedFirstAction), s => s.SelectedFirstAction, (s, v) => s.SelectedFirstAction = v)),
            Card(Translator.NotificationSettings_Mail_SecondAction_Title, Translator.NotificationSettings_Mail_SecondAction_Description, WinoIconGlyph.None,
                OptionPopUp(vm, s => s.MailActionOptions, nameof(vm.SelectedSecondAction), s => s.SelectedSecondAction, (s, v) => s.SelectedSecondAction = v)),
            Card(Translator.NotificationSound_Title, Translator.NotificationSound_Description, WinoIconGlyph.None,
                TypeSoundPicker(NotificationSoundKind.Mail)));
        Bind.Bind(vm, nameof(vm.MailSummary), s => s.MailSummary, text => mail.HeaderCard.Description = text);

        // Calendar.
        var calendar = Expander(Translator.NotificationSettings_Calendar_Title, null, WinoIconGlyph.Calendar,
            Bind.Switch(p, nameof(p.AreCalendarRemindersEnabled), s => s.AreCalendarRemindersEnabled, (s, v) => { s.AreCalendarRemindersEnabled = v; vm.UpdateSectionSummaries(); }, Translator.NotificationSettings_Calendar_Title),
            Card(Translator.CalendarSettings_DefaultReminder_Header, Translator.CalendarSettings_DefaultReminder_Description, WinoIconGlyph.None,
                Bind.PopUp(vm, vm.ReminderOptions, nameof(vm.SelectedReminderIndex), s => s.SelectedReminderIndex, (s, v) => s.SelectedReminderIndex = v, 170)),
            Card(Translator.CalendarSettings_DefaultSnoozeDuration_Header, Translator.CalendarSettings_DefaultSnoozeDuration_Description, WinoIconGlyph.None,
                Bind.PopUp(vm, vm.CalendarSnoozeOptions, nameof(vm.SelectedCalendarSnoozeIndex), s => s.SelectedCalendarSnoozeIndex, (s, v) => s.SelectedCalendarSnoozeIndex = v, 170)),
            Card(Translator.NotificationSound_Title, Translator.NotificationSound_Description, WinoIconGlyph.None,
                TypeSoundPicker(NotificationSoundKind.Calendar)));
        Bind.Bind(vm, nameof(vm.CalendarSummary), s => s.CalendarSummary, text => calendar.HeaderCard.Description = text);

        // Tasks.
        var tasks = Expander(Translator.NotificationSettings_Tasks_Title, null, WinoIconGlyph.TaskList,
            Bind.Switch(p, nameof(p.AreTaskRemindersEnabled), s => s.AreTaskRemindersEnabled, (s, v) => { s.AreTaskRemindersEnabled = v; vm.UpdateSectionSummaries(); }, Translator.NotificationSettings_Tasks_Title),
            Card(Translator.NotificationSettings_Tasks_Timing_Title, Translator.NotificationSettings_Tasks_Timing_Description, WinoIconGlyph.None,
                OptionPopUp(vm, s => s.TaskReminderTimingOptions, nameof(vm.SelectedTaskReminderTiming), s => s.SelectedTaskReminderTiming, (s, v) => s.SelectedTaskReminderTiming = v)),
            Card(Translator.NotificationSettings_Tasks_Snooze_Title, Translator.NotificationSettings_Tasks_Snooze_Description, WinoIconGlyph.None,
                Bind.PopUp(vm, vm.TaskSnoozeOptions, nameof(vm.SelectedTaskSnoozeIndex), s => s.SelectedTaskSnoozeIndex, (s, v) => s.SelectedTaskSnoozeIndex = v, 170)),
            Card(Translator.NotificationSound_Title, Translator.NotificationSound_Description, WinoIconGlyph.None,
                TypeSoundPicker(NotificationSoundKind.Task)));
        Bind.Bind(vm, nameof(vm.TaskSummary), s => s.TaskSummary, text => tasks.HeaderCard.Description = text);

        AddGroup(null, quiet, mail, calendar, tasks);

        // Per-account overrides.
        var accountsHeader = new WinoSettingsGroup(Translator.NotificationSettings_Accounts_Title, Translator.NotificationSettings_Accounts_Description);
        accountsHeader.Surface.Hidden = true;
        Add(accountsHeader);
        _accounts.Alignment = NSLayoutAttribute.Leading;
        Add(_accounts);
        Bind.Collection(vm.Accounts, RebuildAccounts);

        AddGroup(null, Card(Translator.NotificationSettings_Reset_Title, Translator.NotificationSettings_Reset_Description, WinoIconGlyph.ArrowReset,
            Bind.Button(Translator.NotificationSettings_Reset_Title, ResetAsync)));
    }

    /// <summary>
    /// The app-wide sync method: instant push (on) or timer-based synchronization (off). Normal operation needs no
    /// status line, so the card shows messages only when something needs attention (Windows NotificationSettingsPage).
    /// </summary>
    private void AddInstantPush()
    {
        var vm = ViewModel;
        NSView? toggle = null;
        if (vm.IsPushRelayToggleVisible)
            toggle = Bind.Enabled(
                Bind.Switch(vm, nameof(vm.IsPushRelayEnabled), s => s.IsPushRelayEnabled, (s, v) => s.IsPushRelayEnabled = v, Translator.NotificationSettings_RealtimePush_Title),
                vm, nameof(vm.IsPushRelayToggleEnabled), s => s.IsPushRelayToggleEnabled);
        AddGroup(null, Card(Translator.NotificationSettings_RealtimePush_Title, Translator.NotificationSettings_RealtimePush_Description, WinoIconGlyph.Flash, toggle));

        var signIn = InfoBar(WinoInfoBarSeverity.Informational, null, Translator.NotificationSettings_PushRelay_SignInRequired);
        Bind.Visible(signIn, vm, nameof(vm.IsPushRelaySignInRequired), s => s.IsPushRelaySignInRequired);
        Add(signIn);

        Add(TextInfoBar(nameof(vm.PushRelayProblemText), s => s.PushRelayProblemText));
        Add(TextInfoBar(nameof(vm.PushRelayMailboxWarningText), s => s.PushRelayMailboxWarningText));
    }

    /// <summary>A warning bar that shows a view model text and hides while it is empty.</summary>
    private WinoInfoBar TextInfoBar(string property, Func<NotificationSettingsPageViewModel, string?> read)
    {
        var bar = InfoBar(WinoInfoBarSeverity.Warning, null, null);
        Bind.Bind(ViewModel, property, read, text =>
        {
            bar.Message = text;
            bar.Hidden = string.IsNullOrEmpty(text);
        });
        return bar;
    }

    protected override Task InitializeAsync(Core.Domain.Models.Navigation.NavigationMode mode, object? parameter)
    {
        ViewModel.OnNavigatedTo(mode, parameter!);
        // Snooze expiry is evaluated lazily, so the page refreshes it while it is open (Windows uses a 30 s timer).
        _snoozeTimer = NSTimer.CreateRepeatingScheduledTimer(TimeSpan.FromSeconds(30), _ => ViewModel.RefreshSnoozeState());
        return Task.CompletedTask;
    }

    protected override Task DeactivateAsync()
    {
        _snoozeTimer?.Invalidate();
        _snoozeTimer = null;
        return base.DeactivateAsync();
    }

    private async void ResetAsync()
    {
        try
        {
            if (await ViewModel.ConfirmResetAsync(dialogs))
            {
                await Dispatcher.ExecuteOnUIThread(() =>
                {
                    foreach (var refresh in _soundRefreshers.Concat(_accountSoundRefreshers)) refresh();
                });
            }
        }
        catch (Exception exception) { ReportError(exception); }
    }

    private NSPopUpButton OptionPopUp<TSource, TOption>(TSource source, Func<TSource, IReadOnlyList<TOption>?> options, string property,
        Func<TSource, TOption?> read, Action<TSource, TOption> write, SettingsBinder? binder = null)
        where TSource : System.ComponentModel.INotifyPropertyChanged where TOption : NotificationOptionBase
        => (binder ?? Bind).PopUp(source, options, option => option.DisplayText, property, read, write, width: 170);

    private NSDatePicker TimePicker(IPreferencesService source, string property, Func<IPreferencesService, TimeSpan> read, Action<IPreferencesService, TimeSpan> write, string label)
    {
        var picker = new NSDatePicker
        {
            DatePickerStyle = NSDatePickerStyle.TextFieldAndStepper,
            DatePickerElements = NSDatePickerElementFlags.HourMinute,
            Bezeled = true,
            DrawsBackground = true,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        WinoAccessibility.Label(picker, label);
        Bind.Bind(source, property, read, value => picker.DateValue = (NSDate)DateTime.SpecifyKind(DateTime.Today.Add(value), DateTimeKind.Local).ToUniversalTime());
        Bind.OnActivated(picker, () =>
        {
            var time = ((DateTime)picker.DateValue).ToLocalTime().TimeOfDay;
            var trimmed = new TimeSpan(time.Hours, time.Minutes, 0);
            if (read(source) != trimmed) write(source, trimmed);
            ViewModel.UpdateQuietHoursSummary();
        });
        return picker;
    }

    #region Sounds

    private NSView TypeSoundPicker(NotificationSoundKind kind)
        => SoundPicker(() => ViewModel.GetSound(kind), value => ViewModel.SetSound(kind, value), _soundRefreshers);

    private NSView AccountSoundPicker(AccountNotificationSettingsViewModel account)
        => SoundPicker(() => ViewModel.GetAccountSound(account.AccountId), value => ViewModel.SetAccountSound(account.AccountId, value), _accountSoundRefreshers);

    /// <summary>
    /// A pop-up of Default, None and the macOS alert sounds, with an icon-only Play button. Choosing a sound
    /// saves it and plays it, as System Settings › Sound does.
    /// </summary>
    private NSView SoundPicker(Func<string> read, Action<string> write, List<Action> refreshers)
    {
        var choices = ViewModel.SoundChoices;
        var values = new List<string?>();
        foreach (var choice in choices.Where(choice => choice.IsDefaultOrNone)) values.Add(choice.Id);
        values.Add(null);
        foreach (var choice in choices.Where(choice => !choice.IsDefaultOrNone)) values.Add(choice.Id);
        var popUp = new NSPopUpButton { PullsDown = false, TranslatesAutoresizingMaskIntoConstraints = false };
        popUp.Menu!.AutoEnablesItems = false;
        foreach (var value in values)
        {
            if (value is null) { popUp.Menu.AddItem(NSMenuItem.SeparatorItem); continue; }
            popUp.Menu.AddItem(new NSMenuItem(SoundTitle(value)) { RepresentedObject = new NSString(value) });
        }
        popUp.WidthAnchor.ConstraintEqualTo(170).Active = true;
        WinoAccessibility.Label(popUp, Translator.NotificationSound_Title);

        void Select()
        {
            var current = read();
            var index = values.FindIndex(value => value is not null && string.Equals(value, current, StringComparison.OrdinalIgnoreCase));
            popUp.SelectItem(index < 0 ? 0 : index);
        }
        Select();
        refreshers.Add(Select);

        popUp.Activated += (_, _) =>
        {
            if (popUp.SelectedItem?.RepresentedObject is not NSString chosen) return;
            var value = chosen.ToString();
            try
            {
                write(value);
                _ = ViewModel.PlaySoundAsync(value);
            }
            catch (Exception exception) { ReportError(exception); }
        };

        var play = new NSButton
        {
            BezelStyle = NSBezelStyle.Rounded,
            Image = WinoIcons.Image(WinoIconGlyph.Play, 14, null, Translator.NotificationSound_Play),
            ImagePosition = NSCellImagePosition.ImageOnly,
            ToolTip = Translator.NotificationSound_Play,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        WinoAccessibility.Label(play, Translator.NotificationSound_Play);
        play.Activated += (_, _) => _ = ViewModel.PlaySoundAsync(read());
        return Row(popUp, play);
    }

    private string SoundTitle(string id)
        => ViewModel.SoundChoices.FirstOrDefault(choice => string.Equals(choice.Id, id, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? id;

    #endregion

    private void RebuildAccounts()
    {
        _accountScope?.Dispose();
        _accountScope = Bindings.Own(new BindingScope());
        var rows = Bind.Child(_accountScope);
        foreach (var view in _accounts.ArrangedSubviews) { _accounts.RemoveArrangedSubview(view); view.RemoveFromSuperview(); }
        _accountSoundRefreshers.Clear();

        foreach (var account in ViewModel.Accounts.ToList())
        {
            bool Overrides(AccountNotificationSettingsViewModel a) => a.AreOverrideFieldsEnabled;
            bool Enabled(AccountNotificationSettingsViewModel a) => a.AreNotificationsEnabled;
            var sources = new[] { Translator.NotificationSettings_Account_UseDefaults, Translator.NotificationSettings_Account_Customise };
            var expander = new WinoSettingsExpander(account.Account.Name ?? string.Empty, account.AccountAddress, WinoIconGlyph.None,
                rows.Switch(account, nameof(account.AreNotificationsEnabled), Enabled, (a, v) => a.AreNotificationsEnabled = v, account.Account.Name));
            // The account's picture or provider logo, as on the account details and unread badge pages.
            expander.HeaderCard.LeadingView = new WinoAccountIconView(28) { Account = MailAccountIconInfoFactory.Create(account.Account, pictures) };
            rows.Bind(account, nameof(account.StatusText), a => a.StatusText, text => expander.HeaderCard.Description = string.IsNullOrEmpty(text) ? account.AccountAddress : text);
            expander.Add(rows.Enabled(new WinoSettingsCard(Translator.NotificationSettings_Account_Source_Title, Translator.NotificationSettings_Account_Source_Description, WinoIconGlyph.None,
                rows.PopUp(account, sources, nameof(account.HasCustomNotificationSettings), a => a.HasCustomNotificationSettings ? 1 : 0, (a, v) => a.HasCustomNotificationSettings = v == 1, 170)),
                account, nameof(account.AreNotificationsEnabled), Enabled));
            expander.Add(rows.Enabled(new WinoSettingsCard(Translator.NotificationSettings_Account_NewMail_Title, Translator.NotificationSettings_Account_NewMail_Description, WinoIconGlyph.None,
                rows.Switch(account, nameof(account.IsNewMailNotificationEnabled), a => a.IsNewMailNotificationEnabled, (a, v) => a.IsNewMailNotificationEnabled = v, Translator.NotificationSettings_Account_NewMail_Title)),
                account, nameof(account.AreOverrideFieldsEnabled), Overrides));
            expander.Add(rows.Enabled(new WinoSettingsCard(Translator.NotificationSettings_Mail_Scope_Title, Translator.NotificationSettings_Mail_Scope_Description, WinoIconGlyph.None,
                OptionPopUp(account, a => a.ScopeOptions, nameof(account.SelectedScope), a => a.SelectedScope, (a, v) => a.SelectedScope = v, rows)),
                account, nameof(account.AreOverrideFieldsEnabled), Overrides));
            expander.Add(rows.Enabled(new WinoSettingsCard(Translator.NotificationSettings_Mail_Content_Title, Translator.NotificationSettings_Mail_Content_Description, WinoIconGlyph.None,
                OptionPopUp(account, a => a.ContentOptions, nameof(account.SelectedContent), a => a.SelectedContent, (a, v) => a.SelectedContent = v, rows)),
                account, nameof(account.AreOverrideFieldsEnabled), Overrides));
            expander.Add(rows.Enabled(new WinoSettingsCard(Translator.NotificationSound_Title, Translator.NotificationSettings_Account_Sound_Description, WinoIconGlyph.None,
                AccountSoundPicker(account)),
                account, nameof(account.AreOverrideFieldsEnabled), Overrides));
            expander.Add(rows.Enabled(new WinoSettingsCard(Translator.NotificationSettings_Account_Calendar_Title, Translator.NotificationSettings_Account_Calendar_Description, WinoIconGlyph.None,
                rows.Switch(account, nameof(account.AreCalendarRemindersEnabled), a => a.AreCalendarRemindersEnabled, (a, v) => a.AreCalendarRemindersEnabled = v, Translator.NotificationSettings_Account_Calendar_Title)),
                account, nameof(account.AreOverrideFieldsEnabled), Overrides));
            expander.Add(rows.Enabled(new WinoSettingsCard(Translator.NotificationSettings_Account_QuietHours_Title, Translator.NotificationSettings_Account_QuietHours_Description, WinoIconGlyph.None,
                OptionPopUp(account, a => a.QuietHoursOptions, nameof(account.SelectedQuietHoursStance), a => a.SelectedQuietHoursStance, (a, v) => a.SelectedQuietHoursStance = v, rows)),
                account, nameof(account.AreNotificationsEnabled), Enabled));

            var group = new WinoSettingsGroup();
            group.Add(expander);
            group.TranslatesAutoresizingMaskIntoConstraints = false;
            _accounts.AddArrangedSubview(group);
            group.WidthAnchor.ConstraintEqualTo(_accounts.WidthAnchor).Active = true;
        }
    }
}
