using System.ComponentModel;
using Foundation;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.ViewModels;
using Wino.Core.ViewModels.Data;
using Wino.Mail.Controls.UIKit.Common;
using Wino.Mail.Controls.UIKit.Settings;
using Wino.Platform.Apple.Services;
using Wino.Presentation.Bindings;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Settings;

/// <summary>
/// Notifications: snooze, quiet hours, mail, calendar and task sections, per-account overrides and reset (Windows
/// NotificationSettingsPage). Sounds are the iOS alert sounds stored in the Apple-only keys
/// (<see cref="AppleNotificationSounds"/>), each with a Play preview; Windows' sound values are never written.
/// iOS cannot see whether another app is presenting, so "Snooze while presenting" is left out; Focus modes already
/// hold notifications back at the system level.
/// </summary>
public sealed class NotificationSettingsPageViewController(NotificationSettingsPageViewModel viewModel, IMailDialogService dialogs, AppleNotificationSounds sounds,
    IDispatcher dispatcher, IWinoLogger logger)
    : SettingsPageViewController<NotificationSettingsPageViewModel>(viewModel, dispatcher, logger)
{
    private readonly UIStackView _accounts = WinoLayout.VStack(WinoSettingsStyle.CardSpacing);
    private readonly List<Action> _soundRefreshers = [];
    private readonly List<Action> _accountSoundRefreshers = [];
    private BindingScope? _accountScope;
    private BindingScope? _dayScope;
    private NSTimer? _snoozeTimer;

    protected override void BuildPage()
    {
        var vm = ViewModel;
        var p = vm.PreferencesService;

        // Snooze hero: the switch stays trailing, the preset pop-up sits under the text.
        var snoozeSwitch = new WinoLabeledSwitch();
        WinoAccessibility.Label(snoozeSwitch.Switch, Translator.NotificationSettings_SnoozeAll);
        Bind.Bind(vm, nameof(vm.IsSnoozed), s => s.IsSnoozed, value => snoozeSwitch.IsOn = value);
        Bind.OnActivated(snoozeSwitch.Switch, () =>
        {
            if (snoozeSwitch.IsOn == vm.IsSnoozed) return;
            if (snoozeSwitch.IsOn) vm.StartSnoozeCommand.Execute(vm.SelectedSnoozePreset?.Value ?? NotificationSnoozePreset.UntilTurnedBackOn);
            else vm.EndSnoozeCommand.Execute(null);
        }, UIControlEvent.ValueChanged);
        var presets = Bind.PopUp<NotificationSettingsPageViewModel, SnoozePresetOption>(vm, s => s.SnoozePresets, option => option.DisplayText,
            nameof(vm.SelectedSnoozePreset), s => s.SelectedSnoozePreset, (s, v) => s.StartSnoozeCommand.Execute(v.Value));
        var hero = Card(Translator.NotificationSettings_Hero_Title, null, WinoIconGlyph.AlertOff, snoozeSwitch);
        hero.BottomContent = presets;
        Bind.Bind(vm, nameof(vm.SnoozeSummaryText), s => s.SnoozeSummaryText, text => hero.Description = text);
        var state = Card(string.Empty, null, WinoIconGlyph.None);
        Bind.Bind(vm, nameof(vm.SnoozeStateText), s => s.SnoozeStateText, text => { state.Header = text ?? string.Empty; state.Hidden = string.IsNullOrEmpty(text); });
        AddGroup(null, hero, state);

        var quietTime = InfoBar(WinoInfoBarSeverity.Informational, null, Translator.NotificationSettings_DoNotDisturbOn);
        Bind.Visible(quietTime, vm, nameof(vm.IsSystemQuietTimeActive), s => s.IsSystemQuietTimeActive);
        Add(quietTime);

        // Quiet hours.
        bool QuietOn(IPreferencesService s) => s.AreQuietHoursEnabled;
        var days = WinoLayout.HStack(4);
        days.Distribution = UIStackViewDistribution.FillEqually;
        Bind.Collection(vm.QuietHoursDayToggles, () => RebuildDays(days));
        var hours = Row(TimePicker(p, nameof(p.QuietHoursStart), s => s.QuietHoursStart, (s, v) => s.QuietHoursStart = v, Translator.NotificationSettings_QuietHours_From_Title),
            Caption(Translator.NotificationSettings_QuietHours_To),
            TimePicker(p, nameof(p.QuietHoursEnd), s => s.QuietHoursEnd, (s, v) => s.QuietHoursEnd = v, Translator.NotificationSettings_QuietHours_To));
        // No spacer: the card keeps the row at its own width, so "From … to …" reads from the leading edge.
        foreach (var view in hours.ArrangedSubviews)
        {
            view.SetContentHuggingPriority(1000, UILayoutConstraintAxis.Horizontal);
            // A compact picker reports no intrinsic width and would take the whole row: pin it to its pill.
            if (view is not UIDatePicker picker) continue;
            var pill = picker.SystemLayoutSizeFittingSize(UIView.UILayoutFittingCompressedSize).Width;
            var fitted = picker.WidthAnchor.ConstraintEqualTo((nfloat)Math.Max(pill, WinoStyle.MinimumHitTarget));
            fitted.Priority = 999;
            fitted.Active = true;
        }
        var quiet = Expander(Translator.NotificationSettings_QuietHours_Title, null, WinoIconGlyph.Clock,
            Bind.Switch(p, nameof(p.AreQuietHoursEnabled), QuietOn, (s, v) => { s.AreQuietHoursEnabled = v; vm.UpdateQuietHoursSummary(); }, Translator.NotificationSettings_QuietHours_Title),
            Bind.Enabled(StackedCard(Translator.NotificationSettings_QuietHours_From_Title, Translator.NotificationSettings_QuietHours_From_Description, WinoIconGlyph.None, hours), p, nameof(p.AreQuietHoursEnabled), QuietOn),
            Bind.Enabled(StackedCard(Translator.NotificationSettings_QuietHours_Days_Title, Translator.NotificationSettings_QuietHours_Days_Description, WinoIconGlyph.None, days), p, nameof(p.AreQuietHoursEnabled), QuietOn));
        Bind.Bind(vm, nameof(vm.QuietHoursSummary), s => s.QuietHoursSummary, text => quiet.HeaderCard.Description = text);

        // Mail.
        var mail = Expander(Translator.NotificationSettings_Mail_Title, null, WinoIconGlyph.Mail,
            Bind.Switch(p, nameof(p.AreNewMailNotificationsEnabled), s => s.AreNewMailNotificationsEnabled, (s, v) => { s.AreNewMailNotificationsEnabled = v; vm.UpdateSectionSummaries(); }, Translator.NotificationSettings_Mail_Title),
            StackedCard(Translator.NotificationSettings_Mail_Scope_Title, Translator.NotificationSettings_Mail_Scope_Description, WinoIconGlyph.None,
                OptionPopUp(vm, s => s.ScopeOptions, nameof(vm.SelectedMailScope), s => s.SelectedMailScope, (s, v) => s.SelectedMailScope = v)),
            StackedCard(Translator.NotificationSettings_Mail_Content_Title, Translator.NotificationSettings_Mail_Content_Description, WinoIconGlyph.None,
                OptionPopUp(vm, s => s.ContentOptions, nameof(vm.SelectedMailContent), s => s.SelectedMailContent, (s, v) => s.SelectedMailContent = v)),
            StackedCard(Translator.NotificationSettings_Mail_FirstAction_Title, Translator.NotificationSettings_Mail_FirstAction_Description, WinoIconGlyph.None,
                OptionPopUp(vm, s => s.MailActionOptions, nameof(vm.SelectedFirstAction), s => s.SelectedFirstAction, (s, v) => s.SelectedFirstAction = v)),
            StackedCard(Translator.NotificationSettings_Mail_SecondAction_Title, Translator.NotificationSettings_Mail_SecondAction_Description, WinoIconGlyph.None,
                OptionPopUp(vm, s => s.MailActionOptions, nameof(vm.SelectedSecondAction), s => s.SelectedSecondAction, (s, v) => s.SelectedSecondAction = v)),
            StackedCard(Translator.NotificationSound_Title, Translator.NotificationSound_Description, WinoIconGlyph.None,
                TypeSoundPicker(AppleNotificationSoundKind.Mail)));
        Bind.Bind(vm, nameof(vm.MailSummary), s => s.MailSummary, text => mail.HeaderCard.Description = text);

        // Calendar.
        var calendar = Expander(Translator.NotificationSettings_Calendar_Title, null, WinoIconGlyph.Calendar,
            Bind.Switch(p, nameof(p.AreCalendarRemindersEnabled), s => s.AreCalendarRemindersEnabled, (s, v) => { s.AreCalendarRemindersEnabled = v; vm.UpdateSectionSummaries(); }, Translator.NotificationSettings_Calendar_Title),
            StackedCard(Translator.CalendarSettings_DefaultReminder_Header, Translator.CalendarSettings_DefaultReminder_Description, WinoIconGlyph.None,
                Bind.PopUp(vm, vm.ReminderOptions, nameof(vm.SelectedReminderIndex), s => s.SelectedReminderIndex, (s, v) => s.SelectedReminderIndex = v)),
            StackedCard(Translator.CalendarSettings_DefaultSnoozeDuration_Header, Translator.CalendarSettings_DefaultSnoozeDuration_Description, WinoIconGlyph.None,
                Bind.PopUp(vm, vm.CalendarSnoozeOptions, nameof(vm.SelectedCalendarSnoozeIndex), s => s.SelectedCalendarSnoozeIndex, (s, v) => s.SelectedCalendarSnoozeIndex = v)),
            StackedCard(Translator.NotificationSound_Title, Translator.NotificationSound_Description, WinoIconGlyph.None,
                TypeSoundPicker(AppleNotificationSoundKind.Calendar)));
        Bind.Bind(vm, nameof(vm.CalendarSummary), s => s.CalendarSummary, text => calendar.HeaderCard.Description = text);

        // Tasks.
        var tasks = Expander(Translator.NotificationSettings_Tasks_Title, null, WinoIconGlyph.TaskList,
            Bind.Switch(p, nameof(p.AreTaskRemindersEnabled), s => s.AreTaskRemindersEnabled, (s, v) => { s.AreTaskRemindersEnabled = v; vm.UpdateSectionSummaries(); }, Translator.NotificationSettings_Tasks_Title),
            StackedCard(Translator.NotificationSettings_Tasks_Timing_Title, Translator.NotificationSettings_Tasks_Timing_Description, WinoIconGlyph.None,
                OptionPopUp(vm, s => s.TaskReminderTimingOptions, nameof(vm.SelectedTaskReminderTiming), s => s.SelectedTaskReminderTiming, (s, v) => s.SelectedTaskReminderTiming = v)),
            StackedCard(Translator.NotificationSettings_Tasks_Snooze_Title, Translator.NotificationSettings_Tasks_Snooze_Description, WinoIconGlyph.None,
                Bind.PopUp(vm, vm.TaskSnoozeOptions, nameof(vm.SelectedTaskSnoozeIndex), s => s.SelectedTaskSnoozeIndex, (s, v) => s.SelectedTaskSnoozeIndex = v)),
            StackedCard(Translator.NotificationSound_Title, Translator.NotificationSound_Description, WinoIconGlyph.None,
                TypeSoundPicker(AppleNotificationSoundKind.Task)));
        Bind.Bind(vm, nameof(vm.TaskSummary), s => s.TaskSummary, text => tasks.HeaderCard.Description = text);

        AddGroup(null, quiet, mail, calendar, tasks, LiveNotifications());

        // Per-account overrides.
        var accountsHeader = new WinoSettingsGroup(Translator.NotificationSettings_Accounts_Title, Translator.NotificationSettings_Accounts_Description);
        accountsHeader.Surface.Hidden = true;
        Add(accountsHeader);
        _accounts.Alignment = UIStackViewAlignment.Fill;
        Add(_accounts);
        Bind.Collection(vm.Accounts, RebuildAccounts);

        AddGroup(null, StackedCard(Translator.NotificationSettings_Reset_Title, Translator.NotificationSettings_Reset_Description, WinoIconGlyph.ArrowReset,
            SettingsCardLayout.Leading(Bind.Button(Translator.NotificationSettings_Reset_Title, ResetAsync))));
    }

    /// <summary>
    /// Live notifications (WinoRelay). Always on while a Wino Account is signed in, so iOS has no switch: the expander
    /// shows the status, a sign-in hint, mailboxes the relay could not sign in to, and a test push.
    /// </summary>
    private WinoSettingsExpander LiveNotifications()
    {
        var vm = ViewModel;
        var signIn = InfoBar(WinoInfoBarSeverity.Informational, null, Translator.NotificationSettings_PushRelay_SignInRequired);
        Bind.Visible(signIn, vm, nameof(vm.IsPushRelaySignInRequired), s => s.IsPushRelaySignInRequired);

        var status = Card(string.Empty, null, WinoIconGlyph.Info);
        Bind.Bind(vm, nameof(vm.PushRelayStatusText), s => s.PushRelayStatusText, text => status.Header = text ?? string.Empty);
        // The sign-in hint already says it; the status card would repeat the same sentence.
        Bind.Visible(status, vm, nameof(vm.IsPushRelaySignInRequired), s => !s.IsPushRelaySignInRequired);

        var warning = Card(string.Empty, null, WinoIconGlyph.Warning);
        Bind.Bind(vm, nameof(vm.PushRelayMailboxWarningText), s => s.PushRelayMailboxWarningText, text =>
        {
            warning.Header = text ?? string.Empty;
            warning.Hidden = string.IsNullOrEmpty(text);
        });

        var test = StackedCard(Translator.NotificationSettings_PushRelay_SendTest, null, WinoIconGlyph.Send,
            SettingsCardLayout.Leading(Bind.Button(Translator.NotificationSettings_PushRelay_SendTest, vm.SendPushTestCommand)));
        Bind.Bind(vm, nameof(vm.PushRelayTestResultText), s => s.PushRelayTestResultText,
            text => test.Description = string.IsNullOrEmpty(text) ? null : text);
        Bind.Visible(test, vm, nameof(vm.IsPushTestVisible), s => s.IsPushTestVisible);

        var expander = Expander(Translator.NotificationSettings_PushRelay_Title, Translator.NotificationSettings_PushRelay_Description,
            WinoIconGlyph.Cloud, null, signIn, status, warning, test);
        Bind.Visible(expander, vm, nameof(vm.IsPushRelaySectionVisible), s => s.IsPushRelaySectionVisible);
        return expander;
    }

    protected override Task InitializeAsync(NavigationMode mode, object? parameter)
    {
        ViewModel.OnNavigatedTo(mode, parameter);
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
                sounds.ResetAll(ViewModel.Accounts.Select(account => account.AccountId));
                await Dispatcher.ExecuteOnUIThread(() =>
                {
                    foreach (var refresh in _soundRefreshers.Concat(_accountSoundRefreshers)) refresh();
                });
            }
        }
        catch (Exception exception) { ReportError(exception); }
    }

    /// <summary>The seven quiet-hours day toggles in the culture's week order, as equal-width 44pt toggle buttons.</summary>
    private void RebuildDays(UIStackView days)
    {
        _dayScope?.Dispose();
        _dayScope = Bindings.Own(new BindingScope());
        var rows = Bind.Child(_dayScope);
        foreach (var view in days.ArrangedSubviews) { days.RemoveArrangedSubview(view); view.RemoveFromSuperview(); }

        foreach (var day in ViewModel.QuietHoursDayToggles.ToList())
        {
            var configuration = UIButtonConfiguration.GrayButtonConfiguration;
            configuration.Title = day.DisplayText;
            configuration.TitleLineBreakMode = UILineBreakMode.Clip;
            configuration.ContentInsets = new NSDirectionalEdgeInsets(6, 2, 6, 2);
            configuration.CornerStyle = UIButtonConfigurationCornerStyle.Medium;
            var toggle = new UIButton { Configuration = configuration, ChangesSelectionAsPrimaryAction = true, TranslatesAutoresizingMaskIntoConstraints = false };
            toggle.HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)WinoStyle.MinimumHitTarget).Active = true;
            WinoAccessibility.Label(toggle, day.DisplayText);
            toggle.ConfigurationUpdateHandler = button =>
            {
                if (button.Configuration is not { } updated) return;
                updated.BaseBackgroundColor = button.Selected ? WinoStyle.Accent : null;
                updated.BaseForegroundColor = button.Selected ? UIColor.White : WinoStyle.PrimaryText;
                button.Configuration = updated;
            };
            rows.Bind(day, nameof(day.IsSelected), d => d.IsSelected, value => toggle.Selected = value);
            rows.OnActivated(toggle, () => day.IsSelected = toggle.Selected);
            days.AddArrangedSubview(toggle);
        }
    }

    private UIButton OptionPopUp<TSource, TOption>(TSource source, Func<TSource, IReadOnlyList<TOption>?> options, string property,
        Func<TSource, TOption?> read, Action<TSource, TOption> write, SettingsBinder? binder = null)
        where TSource : INotifyPropertyChanged where TOption : NotificationOptionBase
        => (binder ?? Bind).PopUp(source, options, option => option.DisplayText, property, read, write);

    private UIDatePicker TimePicker(IPreferencesService source, string property, Func<IPreferencesService, TimeSpan> read, Action<IPreferencesService, TimeSpan> write, string label)
    {
        var picker = new UIDatePicker
        {
            Mode = UIDatePickerMode.Time,
            PreferredDatePickerStyle = UIDatePickerStyle.Compact,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        WinoAccessibility.Label(picker, label);
        Bind.Bind(source, property, read, value => picker.Date = (NSDate)DateTime.SpecifyKind(DateTime.Today.Add(value), DateTimeKind.Local).ToUniversalTime());
        Bind.OnActivated(picker, () =>
        {
            var time = ((DateTime)picker.Date).ToLocalTime().TimeOfDay;
            var trimmed = new TimeSpan(time.Hours, time.Minutes, 0);
            if (read(source) != trimmed) write(source, trimmed);
            ViewModel.UpdateQuietHoursSummary();
        }, UIControlEvent.ValueChanged);
        return picker;
    }

    #region Sounds

    private UIView TypeSoundPicker(AppleNotificationSoundKind kind)
        => SoundPicker(() => sounds.Resolve(kind), value => sounds.SetStored(kind, value), _soundRefreshers);

    private UIView AccountSoundPicker(AccountNotificationSettingsViewModel account)
        => SoundPicker(() => account.Account.Preferences is { } preferences ? sounds.ResolveAccount(preferences) : AppleNotificationSounds.DefaultSound,
            value => sounds.SetStoredForAccount(account.AccountId, value), _accountSoundRefreshers);

    /// <summary>
    /// A pull-down of Default, None and the iOS alert sounds, with an icon-only Play button. Choosing a sound saves it
    /// and plays it, as Settings › Sounds does.
    /// </summary>
    private UIView SoundPicker(Func<string> read, Action<string> write, List<Action> refreshers)
    {
        var configuration = UIButtonConfiguration.GrayButtonConfiguration;
        configuration.Indicator = UIButtonConfigurationIndicator.Popup;
        configuration.TitleLineBreakMode = UILineBreakMode.TailTruncation;
        var popUp = new UIButton { Configuration = configuration, ShowsMenuAsPrimaryAction = true, TranslatesAutoresizingMaskIntoConstraints = false };
        popUp.HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)WinoStyle.MinimumHitTarget).Active = true;
        WinoAccessibility.Label(popUp, Translator.NotificationSound_Title);

        void Choose(string value)
        {
            try
            {
                write(value);
                sounds.Play(value);
            }
            catch (Exception exception) { ReportError(exception); }
            Render();
        }

        UIAction Item(string value, string current)
        {
            var action = UIAction.Create(SoundTitle(value), null, null, _ => Choose(value));
            action.State = string.Equals(value, current, StringComparison.OrdinalIgnoreCase) ? UIMenuElementState.On : UIMenuElementState.Off;
            return action;
        }

        void Render()
        {
            var current = read();
            var system = sounds.SystemSounds.Select(sound => (UIMenuElement)Item(sound, current)).ToArray();
            popUp.Menu = UIMenu.Create(
            [
                Item(AppleNotificationSounds.DefaultSound, current),
                Item(AppleNotificationSounds.NoSound, current),
                UIMenu.Create(string.Empty, null, UIMenuIdentifier.None, UIMenuOptions.DisplayInline, system)
            ]);
            if (popUp.Configuration is { } updated)
            {
                updated.Title = SoundTitle(current);
                popUp.Configuration = updated;
            }
        }

        Render();
        refreshers.Add(Render);

        var play = SettingsBinder.CreateButton(string.Empty, icon: WinoIconGlyph.Play);
        WinoLayout.Size(play, WinoStyle.MinimumHitTarget, WinoStyle.MinimumHitTarget);
        WinoAccessibility.Label(play, Translator.NotificationSound_Play);
        Bind.OnActivated(play, () => sounds.Play(read()));

        var row = WinoLayout.HStack(WinoStyle.Space2, popUp, play);
        row.Alignment = UIStackViewAlignment.Center;
        WinoLayout.Priorities(popUp, UILayoutConstraintAxis.Horizontal, 1, 250);
        return row;
    }

    private static string SoundTitle(string value) => value switch
    {
        AppleNotificationSounds.DefaultSound => Translator.NotificationSound_Default,
        AppleNotificationSounds.NoSound => Translator.ContactsShellMac_NotificationSoundNone,
        _ => value
    };

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
            var expander = new WinoSettingsExpander(account.Account.Name ?? string.Empty, account.AccountAddress, WinoIconGlyph.Person,
                rows.Switch(account, nameof(account.AreNotificationsEnabled), Enabled, (a, v) => a.AreNotificationsEnabled = v, account.Account.Name));
            rows.Bind(account, nameof(account.StatusText), a => a.StatusText, text => expander.HeaderCard.Description = string.IsNullOrEmpty(text) ? account.AccountAddress : text);
            expander.Add(rows.Enabled(StackedCard(Translator.NotificationSettings_Account_Source_Title, Translator.NotificationSettings_Account_Source_Description, WinoIconGlyph.None,
                rows.PopUp(account, sources, nameof(account.HasCustomNotificationSettings), a => a.HasCustomNotificationSettings ? 1 : 0, (a, v) => a.HasCustomNotificationSettings = v == 1)),
                account, nameof(account.AreNotificationsEnabled), Enabled));
            expander.Add(rows.Enabled(Card(Translator.NotificationSettings_Account_NewMail_Title, Translator.NotificationSettings_Account_NewMail_Description, WinoIconGlyph.None,
                rows.Switch(account, nameof(account.IsNewMailNotificationEnabled), a => a.IsNewMailNotificationEnabled, (a, v) => a.IsNewMailNotificationEnabled = v, Translator.NotificationSettings_Account_NewMail_Title)),
                account, nameof(account.AreOverrideFieldsEnabled), Overrides));
            expander.Add(rows.Enabled(StackedCard(Translator.NotificationSettings_Mail_Scope_Title, Translator.NotificationSettings_Mail_Scope_Description, WinoIconGlyph.None,
                OptionPopUp(account, a => a.ScopeOptions, nameof(account.SelectedScope), a => a.SelectedScope, (a, v) => a.SelectedScope = v, rows)),
                account, nameof(account.AreOverrideFieldsEnabled), Overrides));
            expander.Add(rows.Enabled(StackedCard(Translator.NotificationSettings_Mail_Content_Title, Translator.NotificationSettings_Mail_Content_Description, WinoIconGlyph.None,
                OptionPopUp(account, a => a.ContentOptions, nameof(account.SelectedContent), a => a.SelectedContent, (a, v) => a.SelectedContent = v, rows)),
                account, nameof(account.AreOverrideFieldsEnabled), Overrides));
            expander.Add(rows.Enabled(StackedCard(Translator.NotificationSound_Title, Translator.NotificationSettings_Account_Sound_Description, WinoIconGlyph.None,
                AccountSoundPicker(account)),
                account, nameof(account.AreOverrideFieldsEnabled), Overrides));
            expander.Add(rows.Enabled(Card(Translator.NotificationSettings_Account_Calendar_Title, Translator.NotificationSettings_Account_Calendar_Description, WinoIconGlyph.None,
                rows.Switch(account, nameof(account.AreCalendarRemindersEnabled), a => a.AreCalendarRemindersEnabled, (a, v) => a.AreCalendarRemindersEnabled = v, Translator.NotificationSettings_Account_Calendar_Title)),
                account, nameof(account.AreOverrideFieldsEnabled), Overrides));
            expander.Add(rows.Enabled(StackedCard(Translator.NotificationSettings_Account_QuietHours_Title, Translator.NotificationSettings_Account_QuietHours_Description, WinoIconGlyph.None,
                OptionPopUp(account, a => a.QuietHoursOptions, nameof(account.SelectedQuietHoursStance), a => a.SelectedQuietHoursStance, (a, v) => a.SelectedQuietHoursStance = v, rows)),
                account, nameof(account.AreNotificationsEnabled), Enabled));

            _accounts.AddArrangedSubview(expander);
        }
    }
}
