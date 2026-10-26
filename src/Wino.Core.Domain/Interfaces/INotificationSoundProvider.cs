#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Notifications;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Notification sounds chosen by sound id, for platforms whose sounds are not the shared
/// <see cref="NotificationSoundEvent"/> values. While nothing is stored, a type or account falls back to its
/// <see cref="NotificationSoundEvent"/> preference through <see cref="MapSoundEvent"/>.
/// </summary>
public interface INotificationSoundProvider
{
    /// <summary>Default, None, then the platform's sounds.</summary>
    IReadOnlyList<NotificationSoundChoice> Choices { get; }

    /// <summary>The sound in effect for a type.</summary>
    string GetSound(NotificationSoundKind kind);

    void SetSound(NotificationSoundKind kind, string id);

    /// <summary>The sound stored for an account's override; empty when the account inherits its sound event.</summary>
    string GetAccountSound(Guid accountId);

    /// <summary>Stores an account's override sound; null or empty inherits.</summary>
    void SetAccountSound(Guid accountId, string? id);

    /// <summary>The sound id used for a shared sound event while no choice is stored.</summary>
    string MapSoundEvent(NotificationSoundEvent soundEvent);

    /// <summary>Removes every type and account choice.</summary>
    void ResetAll(IEnumerable<Guid> accountIds);

    /// <summary>Plays a preview of a sound; None plays nothing.</summary>
    Task PlayAsync(string id);

    event EventHandler? Changed;
}
