using System;
using MailCal.Core.Domain.Entities.Shared;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Models.Notifications;

namespace MailCal.Core.Domain.Interfaces;

/// <summary>
/// Single owner of the question "should this notification be delivered right now?".
/// The settings page, the companion flyout and the notification builder all resolve snooze,
/// quiet hours and per-account overrides through this service so they cannot disagree.
/// </summary>
public interface INotificationPolicyService
{
    /// <summary>
    /// Evaluates the current snooze state. An elapsed snooze is cleared as a side effect, so
    /// expiry needs no timer; whoever reads next heals the stored state.
    /// </summary>
    NotificationSnoozeState GetSnoozeState(DateTimeOffset now);

    /// <summary>
    /// Starts a snooze. <paramref name="customUntilLocal"/> is only read for
    /// <see cref="NotificationSnoozePreset.Custom"/>.
    /// </summary>
    void StartSnooze(NotificationSnoozePreset preset, DateTimeOffset? customUntilLocal, DateTimeOffset now);

    /// <summary>
    /// Ends the snooze and resumes delivery.
    /// </summary>
    void EndSnooze();

    /// <summary>
    /// Whether the quiet hours schedule is currently inside its window.
    /// </summary>
    bool IsQuietNow(DateTimeOffset nowLocal);

    /// <summary>
    /// Whether the given notification should be shown.
    /// </summary>
    NotificationDeliveryDecision Evaluate(NotificationKind kind, MailAccountPreferences? accountPreferences, DateTimeOffset now);
}
