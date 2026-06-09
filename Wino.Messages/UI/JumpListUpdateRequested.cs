using Wino.Core.Domain.Interfaces;

namespace Wino.Messaging.UI;

/// <summary>
/// Raised by the background companion process when taskbar jump list entries should be
/// rebuilt. The jump list is per-application UI state, so the UI process performs the
/// actual update when it receives this forwarded message.
/// </summary>
public record JumpListUpdateRequested : IUIMessage;
