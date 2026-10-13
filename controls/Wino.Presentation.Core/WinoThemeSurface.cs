namespace Wino.Presentation;

/// <summary>A surface a custom Wino theme can recolour (the Windows Custom.xaml palette resources).</summary>
public enum WinoThemeSurface
{
    /// <summary>WinoContentZoneBackgroud: every floating zone.</summary>
    Workspace,
    /// <summary>ReadingPaneBackgroundColorBrush: the reader zone.</summary>
    ReadingPane,
    /// <summary>MailListHeaderBackgroundColor: the mail list date group strip.</summary>
    MailListHeader,
    /// <summary>NavigationViewContentBackground: a tint over the sidebar.</summary>
    Navigation,
    /// <summary>CalendarDefaultHourBackgroundBrush: calendar slots outside working hours.</summary>
    CalendarDefaultHour,
    /// <summary>CalendarWorkHourBackgroundBrush: calendar slots inside working hours.</summary>
    CalendarWorkHour
}
