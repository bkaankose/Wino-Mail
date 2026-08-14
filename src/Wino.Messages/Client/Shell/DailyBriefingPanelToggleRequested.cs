namespace Wino.Messaging.Client.Shell;

/// <summary>
/// Raised when the shell's "Daily briefing" navigation item is invoked.
/// The panel lives in the shell window, above the navigated frame, so the request crosses the
/// frame boundary as a message instead of a direct call.
/// </summary>
public sealed record DailyBriefingPanelToggleRequested;
