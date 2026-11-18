#nullable enable
using System;

namespace Wino.Core.Domain.Models.MailItem;

/// <summary>
/// What the delegator did with an operation: how many requests it queued, and the error that stopped it, if any.
/// The delegator shows nothing; the caller reports the outcome.
/// </summary>
public sealed record WinoRequestDelegationResult(int QueuedCount, Exception? Error)
{
    public bool IsSuccess => Error is null;
}
