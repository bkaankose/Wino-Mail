using System;

namespace Wino.Messaging.UI;

/// <summary>
/// A synchronization changed more of an account's task cache than per-item messages can carry
/// cheaply, so listeners reload that account's tasks instead.
/// </summary>
public sealed record BulkTaskChanged(Guid AccountId) : UIMessageBase<BulkTaskChanged>;
