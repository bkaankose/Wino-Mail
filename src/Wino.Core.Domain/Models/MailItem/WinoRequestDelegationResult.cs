#nullable enable
using System;

namespace Wino.Core.Domain.Models.MailItem;

/// <summary>
/// What the delegator did with a mail operation: how many requests it queued, and the error that stopped it, if any.
/// Callers without a window to show an InfoBar in (the voice bubble) use it to report the outcome themselves.
/// </summary>
public sealed record WinoRequestDelegationResult(int QueuedCount, Exception? Error)
{
    public bool IsSuccess => Error is null;
}
