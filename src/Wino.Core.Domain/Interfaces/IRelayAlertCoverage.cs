#nullable enable
using System;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// On platforms where the relay's push is itself a visible alert (iOS: the Notification Service Extension rewrites it
/// with sender and subject), the app must not post its own new-mail notification for the sync that push started.
/// The relay handler opens a short coverage window per account; the notification builder stays quiet inside it.
/// </summary>
public interface IRelayAlertCoverage
{
    void BeginCoverage(Guid accountId, TimeSpan duration);

    bool IsCovered(Guid accountId);
}
