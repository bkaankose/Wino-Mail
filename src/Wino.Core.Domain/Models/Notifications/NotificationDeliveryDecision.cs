namespace Wino.Core.Domain.Models.Notifications;

/// <summary>
/// Why a notification is or is not delivered. The reason exists so logs and tests can tell the
/// suppression causes apart instead of seeing one opaque false.
/// </summary>
public enum NotificationSuppressionReason
{
    None = 0,
    Snoozed = 1,
    QuietHours = 2,
    AccountDisabled = 3,
    TypeDisabled = 4
}

public sealed record NotificationDeliveryDecision(bool ShouldDeliver, NotificationSuppressionReason Reason)
{
    public static NotificationDeliveryDecision Deliver { get; } = new(true, NotificationSuppressionReason.None);

    public static NotificationDeliveryDecision Suppress(NotificationSuppressionReason reason) => new(false, reason);
}
