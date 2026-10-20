using System;
using Wino.Core.Domain.Models.Synchronization;

namespace Wino.Messaging.Server;

/// <summary>
/// A desktop IMAP account's IDLE loop started listening, lost its connection or stopped. Sent through the
/// default messenger by the synchronizer, so the sync coordinator can keep per-account coverage current.
/// </summary>
public record ImapIdleStateChanged(Guid AccountId, ImapIdleStatus Status);
