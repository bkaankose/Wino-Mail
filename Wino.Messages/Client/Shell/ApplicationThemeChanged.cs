namespace Wino.Messaging.Client.Shell;

/// <summary>
/// When the application theme changed.
/// </summary>
/// <param name="IsUnderlyingThemeDark"></param>
public record ApplicationThemeChanged(bool IsUnderlyingThemeDark);
