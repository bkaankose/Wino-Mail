namespace Wino.Core.Domain.Messaging.Client.Shell;

/// <summary>
/// When the application theme changed.
/// </summary>
/// <param name="IsUnderlyingThemeDark"></param>
public record ApplicationThemeChanged(bool IsUnderlyingThemeDark);
