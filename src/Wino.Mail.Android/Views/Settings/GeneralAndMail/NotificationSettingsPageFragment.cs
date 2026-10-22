using System.ComponentModel;
using Android.OS;
using Android.Views;
using Android.Widget;
using Google.Android.Material.Button;
using Google.Android.Material.MaterialSwitch;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.ViewModels;
using Wino.Core.ViewModels.Data;
using Wino.Mail.Android.Infrastructure;
using Wino.Mail.Controls.Android.Common;
using Wino.Mail.Controls.Android.Settings;
using Wino.Presentation.Android;
using Wino.Presentation.Bindings;

namespace Wino.Mail.Android.Views.Settings.GeneralAndMail;

/// <summary>
/// Notifications: status bars, instant push, snooze, quiet hours, mail, calendar and task sections, per-account overrides and
/// reset (Windows NotificationSettingsPage, iOS NotificationSettingsPageViewController, same order). Android owns the
/// notification permission and each channel's sound, so the iOS sound pickers (Apple-only keys) are left out and Windows'
/// sound values are never written; Android cannot see whether another app is presenting, so "Snooze while presenting" is
/// left out as on iOS. The snooze state is refreshed every 30 s while the page's view exists (Windows timer).
/// </summary>
public sealed class NotificationSettingsPageFragment : SettingsPageFragment<NotificationSettingsPageViewModel>
{
    private static readonly TimeSpan SnoozeRefreshInterval = TimeSpan.FromSeconds(30);

    private WinoSettingsGroup? _accounts;
    private LinearLayout? _days;
    private BindingScope? _accountScope;
    private BindingScope? _dayScope;

    protected override void BuildPage()
    {
        var vm = VM;
        var p = vm.PreferencesService;

        // Page status, above everything: held notifications, then Do Not Disturb (which never says when it ends).
        var snoozed = InfoBar(WinoInfoBarSeverity.Informational, null, null);
        Bind.Bind(vm, nameof(vm.SnoozeSummaryText), s => s.SnoozeSummaryText, text => snoozed.Message = text);
        Bind.Visible(snoozed, vm, nameof(vm.IsSnoozed), s => s.IsSnoozed);
        Add(snoozed);

        var quietTime = InfoBar(WinoInfoBarSeverity.Informational, null, Translator.NotificationSettings_DoNotDisturbOn);
        Bind.Visible(quietTime, vm, nameof(vm.IsSystemQuietTimeActive), s => s.IsSystemQuietTimeActive);
        Add(quietTime);

        if (vm.IsPushRelaySectionVisible)
            AddInstantPush();

        // Snooze: a regular card; the switch stays trailing, the preset dropdown sits under the text. While notifications
        // are held, the status shows at the top of the page instead.
        var snoozeSwitch = WinoMaterialAccent.Tint(new MaterialSwitch(Ctx) { ContentDescription = Translator.NotificationSettings_SnoozeAll });
        Bind.Bind(vm, nameof(vm.IsSnoozed), s => s.IsSnoozed, value => { if (snoozeSwitch.Checked != value) snoozeSwitch.Checked = value; });
        Bind.Views.Click(snoozeSwitch, () =>
        {
            if (snoozeSwitch.Checked == vm.IsSnoozed) return;
            if (snoozeSwitch.Checked) vm.StartSnoozeCommand.Execute(vm.SelectedSnoozePreset?.Value ?? NotificationSnoozePreset.UntilTurnedBackOn);
            else vm.EndSnoozeCommand.Execute(null);
        });
        var presets = Bind.PopUp<NotificationSettingsPageViewModel, SnoozePresetOption>(vm, s => s.SnoozePresets, option => option.DisplayText,
            nameof(vm.SelectedSnoozePreset), s => s.SelectedSnoozePreset, (s, v) => s.StartSnoozeCommand.Execute(v.Value));
        var snooze = Card(Translator.NotificationSettings_Snooze_Title, Translator.NotificationSettings_Snooze_ActiveDescription, WinoIconGlyph.AlertOff, snoozeSwitch);
        snooze.BottomContent = presets;
        AddGroup(null, snooze);

        // Quiet hours.
        static bool QuietOn(IPreferencesService s) => s.AreQuietHoursEnabled;
        _days = new LinearLayout(Ctx) { Orientation = Orientation.Horizontal };
        Bindings.Own(new ActionDisposable(() => _dayScope?.Dispose()));
        Bind.Collection(vm.QuietHoursDayToggles, RebuildDays);
        // Two time rows on the detail page ("Quiet from", "to"), each opening the time picker.
        var hours = Row(
            Bind.TimePicker(p, nameof(p.QuietHoursStart), s => s.QuietHoursStart, (s, v) => { s.QuietHoursStart = v; vm.UpdateQuietHoursSummary(); }, Translator.NotificationSettings_QuietHours_From_Title),
            Bind.TimePicker(p, nameof(p.QuietHoursEnd), s => s.QuietHoursEnd, (s, v) => { s.QuietHoursEnd = v; vm.UpdateQuietHoursSummary(); }, Translator.NotificationSettings_QuietHours_To));
        var quiet = Expander(Translator.NotificationSettings_QuietHours_Title, null, WinoIconGlyph.Clock,
            Bind.Switch(p, nameof(p.AreQuietHoursEnabled), QuietOn, (s, v) => { s.AreQuietHoursEnabled = v; vm.UpdateQuietHoursSummary(); }, Translator.NotificationSettings_QuietHours_Title),
            Bind.Enabled(StackedCard(Translator.NotificationSettings_QuietHours_From_Title, Translator.NotificationSettings_QuietHours_From_Description, WinoIconGlyph.None, hours), p, nameof(p.AreQuietHoursEnabled), QuietOn),
            Bind.Enabled(StackedCard(Translator.NotificationSettings_QuietHours_Days_Title, Translator.NotificationSettings_QuietHours_Days_Description, WinoIconGlyph.None, _days), p, nameof(p.AreQuietHoursEnabled), QuietOn));
        Bind.Bind(vm, nameof(vm.QuietHoursSummary), s => s.QuietHoursSummary, text => quiet.HeaderCard.Description = text);

        // Mail.
        var mail = Expander(Translator.NotificationSettings_Mail_Title, null, WinoIconGlyph.Mail,
            Bind.Switch(p, nameof(p.AreNewMailNotificationsEnabled), s => s.AreNewMailNotificationsEnabled, (s, v) => { s.AreNewMailNotificationsEnabled = v; vm.UpdateSectionSummaries(); }, Translator.NotificationSettings_Mail_Title),
            StackedCard(Translator.NotificationSettings_Mail_Scope_Title, Translator.NotificationSettings_Mail_Scope_Description, WinoIconGlyph.None,
                OptionPopUp(Bind, vm, s => s.ScopeOptions, nameof(vm.SelectedMailScope), s => s.SelectedMailScope, (s, v) => s.SelectedMailScope = v)),
            StackedCard(Translator.NotificationSettings_Mail_Content_Title, Translator.NotificationSettings_Mail_Content_Description, WinoIconGlyph.None,
                OptionPopUp(Bind, vm, s => s.ContentOptions, nameof(vm.SelectedMailContent), s => s.SelectedMailContent, (s, v) => s.SelectedMailContent = v)),
            StackedCard(Translator.NotificationSettings_Mail_FirstAction_Title, Translator.NotificationSettings_Mail_FirstAction_Description, WinoIconGlyph.None,
                OptionPopUp(Bind, vm, s => s.MailActionOptions, nameof(vm.SelectedFirstAction), s => s.SelectedFirstAction, (s, v) => s.SelectedFirstAction = v)),
            StackedCard(Translator.NotificationSettings_Mail_SecondAction_Title, Translator.NotificationSettings_Mail_SecondAction_Description, WinoIconGlyph.None,
                OptionPopUp(Bind, vm, s => s.MailActionOptions, nameof(vm.SelectedSecondAction), s => s.SelectedSecondAction, (s, v) => s.SelectedSecondAction = v)));
        Bind.Bind(vm, nameof(vm.MailSummary), s => s.MailSummary, text => mail.HeaderCard.Description = text);

        // Calendar.
        var calendar = Expander(Translator.NotificationSettings_Calendar_Title, null, WinoIconGlyph.Calendar,
            Bind.Switch(p, nameof(p.AreCalendarRemindersEnabled), s => s.AreCalendarRemindersEnabled, (s, v) => { s.AreCalendarRemindersEnabled = v; vm.UpdateSectionSummaries(); }, Translator.NotificationSettings_Calendar_Title),
            StackedCard(Translator.CalendarSettings_DefaultReminder_Header, Translator.CalendarSettings_DefaultReminder_Description, WinoIconGlyph.None,
                Bind.PopUp(vm, vm.ReminderOptions, nameof(vm.SelectedReminderIndex), s => s.SelectedReminderIndex, (s, v) => s.SelectedReminderIndex = v)),
            StackedCard(Translator.CalendarSettings_DefaultSnoozeDuration_Header, Translator.CalendarSettings_DefaultSnoozeDuration_Description, WinoIconGlyph.None,
                Bind.PopUp(vm, vm.CalendarSnoozeOptions, nameof(vm.SelectedCalendarSnoozeIndex), s => s.SelectedCalendarSnoozeIndex, (s, v) => s.SelectedCalendarSnoozeIndex = v)));
        Bind.Bind(vm, nameof(vm.CalendarSummary), s => s.CalendarSummary, text => calendar.HeaderCard.Description = text);

        // Tasks.
        var tasks = Expander(Translator.NotificationSettings_Tasks_Title, null, WinoIconGlyph.TaskList,
            Bind.Switch(p, nameof(p.AreTaskRemindersEnabled), s => s.AreTaskRemindersEnabled, (s, v) => { s.AreTaskRemindersEnabled = v; vm.UpdateSectionSummaries(); }, Translator.NotificationSettings_Tasks_Title),
            StackedCard(Translator.NotificationSettings_Tasks_Timing_Title, Translator.NotificationSettings_Tasks_Timing_Description, WinoIconGlyph.None,
                OptionPopUp(Bind, vm, s => s.TaskReminderTimingOptions, nameof(vm.SelectedTaskReminderTiming), s => s.SelectedTaskReminderTiming, (s, v) => s.SelectedTaskReminderTiming = v)),
            StackedCard(Translator.NotificationSettings_Tasks_Snooze_Title, Translator.NotificationSettings_Tasks_Snooze_Description, WinoIconGlyph.None,
                Bind.PopUp(vm, vm.TaskSnoozeOptions, nameof(vm.SelectedTaskSnoozeIndex), s => s.SelectedTaskSnoozeIndex, (s, v) => s.SelectedTaskSnoozeIndex = v)));
        Bind.Bind(vm, nameof(vm.TaskSummary), s => s.TaskSummary, text => tasks.HeaderCard.Description = text);

        AddGroup(null, quiet, mail, calendar, tasks);

        // Sounds: Android keeps them per notification channel, so the system settings own them.
        AddGroup(null, StackedCard(Translator.Android_NotificationSettings_System_Title, Translator.Android_NotificationSettings_System_Description, WinoIconGlyph.None,
            Row(Bind.Button(Translator.Android_NotificationSettings_System_Button, OpenSystemNotificationSettings))));

        // Per-account overrides.
        _accounts = Add(new WinoSettingsGroup(Ctx, Translator.NotificationSettings_Accounts_Title, Translator.NotificationSettings_Accounts_Description));
        Bindings.Own(new ActionDisposable(() => _accountScope?.Dispose()));
        Bind.Collection(vm.Accounts, RebuildAccounts);

        AddGroup(null, StackedCard(Translator.NotificationSettings_Reset_Title, Translator.NotificationSettings_Reset_Description, WinoIconGlyph.ArrowReset,
            Row(Bind.Button(Translator.NotificationSettings_Reset_Title, ResetAsync))));

        StartSnoozeRefresh();
    }

    /// <summary>
    /// Instant push (Windows NotificationSettingsPage). Android is always on, so the card has no switch: its description
    /// counts the watched mailboxes, and the bars under it show only what needs attention. Android keeps the test push,
    /// which needs an FCM token the desktop heads do not have.
    /// </summary>
    private void AddInstantPush()
    {
        var vm = VM;
        var card = Card(Translator.NotificationSettings_InstantPush_Title, null, WinoIconGlyph.Flash);
        Bind.Bind(vm, nameof(vm.InstantPushDescription), s => s.InstantPushDescription,
            text => card.Description = string.IsNullOrEmpty(text) ? null : text);

        var test = StackedCard(Translator.NotificationSettings_PushRelay_SendTest, null, WinoIconGlyph.Send,
            Row(Bind.Button(Translator.NotificationSettings_PushRelay_SendTest, vm.SendPushTestCommand)));
        Bind.Bind(vm, nameof(vm.PushRelayTestResultText), s => s.PushRelayTestResultText,
            text => test.Description = string.IsNullOrEmpty(text) ? null : text);
        Bind.Visible(test, vm, nameof(vm.IsPushTestVisible), s => s.IsPushTestVisible);
        AddGroup(null, card, test);

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
        Bind.Bind(VM, property, read, text =>
        {
            bar.Message = text;
            bar.Visibility = string.IsNullOrEmpty(text) ? ViewStates.Gone : ViewStates.Visible;
        });
        return bar;
    }

    /// <summary>Snooze expiry is evaluated lazily, so the page refreshes it while its view exists (Windows uses a 30 s timer).</summary>
    private void StartSnoozeRefresh()
    {
        if (Looper.MainLooper is not { } looper) return;
        var handler = new Handler(looper);
        var vm = VM;
        Java.Lang.IRunnable? tick = null;
        tick = new Java.Lang.Runnable(() =>
        {
            try { vm.RefreshSnoozeState(); }
            catch (Exception exception) { ReportError(exception); }
            if (tick is not null) handler.PostDelayed(tick, (long)SnoozeRefreshInterval.TotalMilliseconds);
        });
        handler.PostDelayed(tick, (long)SnoozeRefreshInterval.TotalMilliseconds);
        Bindings.Own(new ActionDisposable(() =>
        {
            handler.RemoveCallbacksAndMessages(null);
            tick = null;
        }));
    }

    /// <summary>Opens Wino's notification settings in the system Settings app (channels, sounds, vibration).</summary>
    private void OpenSystemNotificationSettings()
    {
        // Notification channels (and their settings page) arrived in Android 8; older versions show the app's details.
        var intent = OperatingSystem.IsAndroidVersionAtLeast(26)
            ? new global::Android.Content.Intent(global::Android.Provider.Settings.ActionAppNotificationSettings)
                .PutExtra(global::Android.Provider.Settings.ExtraAppPackage, Ctx.PackageName)
            : new global::Android.Content.Intent(global::Android.Provider.Settings.ActionApplicationDetailsSettings,
                global::Android.Net.Uri.Parse("package:" + Ctx.PackageName));
        Ctx.StartActivity(intent);
    }

    private async void ResetAsync()
    {
        try
        {
            if (MainApplication.Services?.GetService<IMailDialogService>() is not { } dialogs) return;
            await VM.ConfirmResetAsync(dialogs);
        }
        catch (Exception exception) { ReportError(exception); }
    }

    /// <summary>The seven quiet-hours day toggles in the culture's week order, as equal-width checkable buttons.</summary>
    private void RebuildDays()
    {
        if (_days is not { } days) return;
        _dayScope?.Dispose();
        _dayScope = new BindingScope();
        var rows = Bind.Child(_dayScope);
        days.RemoveAllViews();

        foreach (var day in VM.QuietHoursDayToggles.ToList())
        {
            var toggle = new MaterialButton(Ctx, null, Resource.Attribute.materialButtonOutlinedStyle)
            {
                Text = day.DisplayText,
                Checkable = true,
                ContentDescription = day.DisplayText
            };
            toggle.SetAllCaps(false);
            toggle.SetMaxLines(1);
            toggle.InsetTop = 0;
            toggle.InsetBottom = 0;
            toggle.SetPadding(0, toggle.PaddingTop, 0, toggle.PaddingBottom);
            toggle.SetMinWidth(0);
            toggle.SetMinimumWidth(0);
            toggle.CornerRadius = WinoTokens.Px(Ctx, Resource.Dimension.wino_radius_small);
            WinoMaterialAccent.TintSegment(toggle);
            rows.Bind(day, nameof(day.IsSelected), d => d.IsSelected, value => { if (toggle.Checked != value) toggle.Checked = value; });
            rows.Views.Click(toggle, () => day.IsSelected = toggle.Checked);
            var layout = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1);
            if (days.ChildCount > 0) layout.MarginStart = WinoTokens.Px(Ctx, Resource.Dimension.wino_space_2xs);
            days.AddView(toggle, layout);
        }
    }

    private static MaterialButton OptionPopUp<TSource, TOption>(SettingsBinder binder, TSource source, Func<TSource, IReadOnlyList<TOption>?> options, string property,
        Func<TSource, TOption?> read, Action<TSource, TOption> write)
        where TSource : INotifyPropertyChanged where TOption : NotificationOptionBase
        => binder.PopUp(source, options, option => option.DisplayText, property, read, write);

    private void RebuildAccounts()
    {
        if (_accounts is not { } accounts) return;
        _accountScope?.Dispose();
        _accountScope = new BindingScope();
        var rows = Bind.Child(_accountScope);
        accounts.Clear();

        foreach (var account in VM.Accounts.ToList())
        {
            static bool Overrides(AccountNotificationSettingsViewModel a) => a.AreOverrideFieldsEnabled;
            static bool Enabled(AccountNotificationSettingsViewModel a) => a.AreNotificationsEnabled;
            var sources = new[] { Translator.NotificationSettings_Account_UseDefaults, Translator.NotificationSettings_Account_Customise };
            var expander = new WinoSettingsExpander(Ctx, account.Account.Name ?? string.Empty, account.AccountAddress, WinoIconGlyph.Person,
                rows.Switch(account, nameof(account.AreNotificationsEnabled), Enabled, (a, v) => a.AreNotificationsEnabled = v, account.Account.Name));
            rows.Bind(account, nameof(account.StatusText), a => a.StatusText, text => expander.HeaderCard.Description = string.IsNullOrEmpty(text) ? account.AccountAddress : text);
            expander.Add(rows.Enabled(StackedCard(Translator.NotificationSettings_Account_Source_Title, Translator.NotificationSettings_Account_Source_Description, WinoIconGlyph.None,
                rows.PopUp(account, sources, nameof(account.HasCustomNotificationSettings), a => a.HasCustomNotificationSettings ? 1 : 0, (a, v) => a.HasCustomNotificationSettings = v == 1)),
                account, nameof(account.AreNotificationsEnabled), Enabled));
            expander.Add(rows.Enabled(Card(Translator.NotificationSettings_Account_NewMail_Title, Translator.NotificationSettings_Account_NewMail_Description, WinoIconGlyph.None,
                rows.Switch(account, nameof(account.IsNewMailNotificationEnabled), a => a.IsNewMailNotificationEnabled, (a, v) => a.IsNewMailNotificationEnabled = v, Translator.NotificationSettings_Account_NewMail_Title)),
                account, nameof(account.AreOverrideFieldsEnabled), Overrides));
            expander.Add(rows.Enabled(StackedCard(Translator.NotificationSettings_Mail_Scope_Title, Translator.NotificationSettings_Mail_Scope_Description, WinoIconGlyph.None,
                OptionPopUp(rows, account, a => a.ScopeOptions, nameof(account.SelectedScope), a => a.SelectedScope, (a, v) => a.SelectedScope = v)),
                account, nameof(account.AreOverrideFieldsEnabled), Overrides));
            expander.Add(rows.Enabled(StackedCard(Translator.NotificationSettings_Mail_Content_Title, Translator.NotificationSettings_Mail_Content_Description, WinoIconGlyph.None,
                OptionPopUp(rows, account, a => a.ContentOptions, nameof(account.SelectedContent), a => a.SelectedContent, (a, v) => a.SelectedContent = v)),
                account, nameof(account.AreOverrideFieldsEnabled), Overrides));
            expander.Add(rows.Enabled(Card(Translator.NotificationSettings_Account_Calendar_Title, Translator.NotificationSettings_Account_Calendar_Description, WinoIconGlyph.None,
                rows.Switch(account, nameof(account.AreCalendarRemindersEnabled), a => a.AreCalendarRemindersEnabled, (a, v) => a.AreCalendarRemindersEnabled = v, Translator.NotificationSettings_Account_Calendar_Title)),
                account, nameof(account.AreOverrideFieldsEnabled), Overrides));
            expander.Add(rows.Enabled(StackedCard(Translator.NotificationSettings_Account_QuietHours_Title, Translator.NotificationSettings_Account_QuietHours_Description, WinoIconGlyph.None,
                OptionPopUp(rows, account, a => a.QuietHoursOptions, nameof(account.SelectedQuietHoursStance), a => a.SelectedQuietHoursStance, (a, v) => a.SelectedQuietHoursStance = v)),
                account, nameof(account.AreNotificationsEnabled), Enabled));

            accounts.Add(expander);
        }
    }
}
