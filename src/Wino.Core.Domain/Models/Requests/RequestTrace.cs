using System;

namespace Wino.Core.Domain.Models.Requests;

/// <summary>
/// Identifies the external message that produced a queued provider request.
/// The context travels with the request through undo, batching, and retry paths.
/// </summary>
public sealed record RequestTrace(string Source, Guid MessageId);
