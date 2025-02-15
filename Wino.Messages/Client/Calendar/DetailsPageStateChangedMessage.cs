namespace Wino.Messaging.Client.Calendar;

/// <summary>
/// When event details page is activated or deactivated.
/// </summary>
public record DetailsPageStateChangedMessage(bool IsActivated);
