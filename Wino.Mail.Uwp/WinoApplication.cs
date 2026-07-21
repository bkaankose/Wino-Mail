namespace Wino.Mail.Uwp;

/// <summary>
/// Source-compatibility facade for UI classes moved from the desktop project. It
/// represents the single UWP Application instance and does not manage windows.
/// </summary>
public static class WinoApplication
{
    public static App Current => App.Current;
}
