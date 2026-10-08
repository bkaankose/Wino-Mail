using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Domain.Models.Notifications;

/// <summary>
/// The notification settings that actually apply to an account, after the app-wide defaults and
/// the account's own overrides have been reconciled.
/// </summary>
public sealed record EffectiveMailNotificationSettings(
    bool IsEnabled,
    MailNotificationScope Scope,
    MailNotificationContent Content,
    NotificationSoundEvent Sound,
    AccountQuietHoursStance QuietHoursStance);

/// <summary>
/// Reconciles app-wide notification defaults with per-account overrides.
/// Kept pure and static so the notification builder and the settings UI resolve values identically.
/// </summary>
public static class NotificationSettingsResolver
{
    public static EffectiveMailNotificationSettings ResolveMail(IPreferencesService defaults, MailAccountPreferences? account)
    {
        // No account means a notification that is not about one mailbox, such as the aggregate
        // "N new messages" toast. Those follow the app-wide defaults.
        if (account is null)
        {
            return new EffectiveMailNotificationSettings(
                IsEnabled: defaults.AreNewMailNotificationsEnabled,
                Scope: defaults.MailNotificationScope,
                Content: defaults.MailNotificationContent,
                Sound: defaults.MailNotificationSoundEvent,
                QuietHoursStance: AccountQuietHoursStance.Follow);
        }

        // An account with notifications switched off is off regardless of anything else.
        if (!account.IsNotificationsEnabled)
        {
            return new EffectiveMailNotificationSettings(
                IsEnabled: false,
                Scope: defaults.MailNotificationScope,
                Content: defaults.MailNotificationContent,
                Sound: defaults.MailNotificationSoundEvent,
                QuietHoursStance: account.QuietHoursStance);
        }

        if (!account.HasCustomNotificationSettings)
        {
            return new EffectiveMailNotificationSettings(
                IsEnabled: defaults.AreNewMailNotificationsEnabled,
                Scope: defaults.MailNotificationScope,
                Content: defaults.MailNotificationContent,
                Sound: defaults.MailNotificationSoundEvent,
                QuietHoursStance: account.QuietHoursStance);
        }

        return new EffectiveMailNotificationSettings(
            IsEnabled: defaults.AreNewMailNotificationsEnabled && account.IsNewMailNotificationEnabled,
            Scope: account.NotificationScope,
            Content: account.NotificationContent,
            Sound: account.AccountNotificationSoundEvent,
            QuietHoursStance: account.QuietHoursStance);
    }

    public static bool ResolveCalendarRemindersEnabled(IPreferencesService defaults, MailAccountPreferences? account)
    {
        if (account is null)
            return defaults.AreCalendarRemindersEnabled;

        if (!account.IsNotificationsEnabled)
            return false;

        if (!account.HasCustomNotificationSettings)
            return defaults.AreCalendarRemindersEnabled;

        return defaults.AreCalendarRemindersEnabled && account.AreCalendarRemindersEnabled;
    }
}
