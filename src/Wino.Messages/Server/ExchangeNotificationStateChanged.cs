using System;
using Wino.Core.Domain.Models.Synchronization;

namespace Wino.Messaging.Server;

/// <summary>
/// A desktop Exchange account's change listener started listening, lost its session or stopped. Sent through the
/// default messenger by the synchronizer, so the sync coordinator can keep per-account coverage current.
/// </summary>
public record ExchangeNotificationStateChanged(Guid AccountId, ExchangeNotificationStatus Status);
