#nullable enable
using System;
using System.Collections.Generic;

namespace Wino.Core.Domain.Models.Notifications;

/// <summary>How the user answered a notification.</summary>
public enum NotificationResponseKind
{
    /// <summary>The notification body was tapped or clicked.</summary>
    Default,

    /// <summary>The notification was dismissed or cleared.</summary>
    Dismiss,

    /// <summary>One of the notification's action buttons was used (see <see cref="NotificationResponse.ActionIdentifier"/>).</summary>
    Action
}

/// <summary>
/// A platform-neutral copy of a notification response. Each head reduces its native response
/// (UNNotificationResponse, an AppNotification activation) to this record and hands it to
/// <see cref="Interfaces.INotificationResponseService"/>.
/// </summary>
/// <param name="Kind">How the user answered.</param>
/// <param name="ActionIdentifier">The action identifier from <see cref="NotificationActionIdentifiers"/>; empty for a default tap.</param>
/// <param name="Category">The notification category identifier, when the platform has one.</param>
/// <param name="UserText">Text typed into an inline reply field, if any.</param>
/// <param name="Values">The notification payload (keys from <see cref="Constants"/>, for example <see cref="Constants.ToastMailUniqueIdKey"/>).</param>
public sealed record NotificationResponse(
    NotificationResponseKind Kind,
    string ActionIdentifier,
    string? Category,
    string? UserText,
    IReadOnlyDictionary<string, string> Values)
{
    /// <summary>The notification body was used, or the explicit Open action.</summary>
    public bool IsOpen => Kind == NotificationResponseKind.Default ||
                          string.Equals(ActionIdentifier, NotificationActionIdentifiers.Open, StringComparison.Ordinal);

    /// <summary>The notification was dismissed, or the explicit Dismiss action.</summary>
    public bool IsDismiss => Kind == NotificationResponseKind.Dismiss ||
                             string.Equals(ActionIdentifier, NotificationActionIdentifiers.Dismiss, StringComparison.Ordinal);
}
