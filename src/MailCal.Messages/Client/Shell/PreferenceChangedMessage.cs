namespace MailCal.Messaging.Client.Shell;

/// <summary>
/// Raised by the preferences service whenever a stored preference value changes.
/// View models that cache a preference for runtime logic listen to this to stay current
/// while settings are edited elsewhere, such as the separate macOS Settings window.
/// </summary>
/// <param name="PropertyName">Name of the changed IPreferencesService property.</param>
public sealed record PreferenceChangedMessage(string PropertyName);
