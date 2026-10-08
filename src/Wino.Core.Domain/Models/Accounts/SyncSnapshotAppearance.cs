namespace Wino.Core.Domain.Models.Accounts;

/// <summary>
/// Theme and layout state carried by a sync snapshot. Enums travel as their integer values.
/// </summary>
public sealed class SyncSnapshotAppearance
{
    public int RootTheme { get; set; }
    public System.Guid? ThemeId { get; set; }
    public string? AccentColor { get; set; }
    public int BackdropType { get; set; }
    public double OpenPaneLength { get; set; }
    public double MailListPaneLength { get; set; }
    public int CalendarDisplayType { get; set; }
    public int DayDisplayCount { get; set; }
}
