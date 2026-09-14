using System.Collections.Generic;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Personalization;

/// <summary>
/// Describes every editable custom theme color: the section it belongs to, the miniature
/// surface that previews it, and the plain-language explanation of where it shows up.
/// </summary>
public static class CustomThemeColorCatalog
{
    /// <summary>
    /// The surface colors offered under the Surfaces section, in editing order.
    /// </summary>
    public static IReadOnlyList<CustomThemeColorKey> SurfaceKeys { get; } = (CustomThemeColorKey[])
    [
        CustomThemeColorKey.Navigation,
        CustomThemeColorKey.Workspace,
        CustomThemeColorKey.MailListHeader,
        CustomThemeColorKey.ReadingPane
    ];

    /// <summary>
    /// The calendar slot colors offered under the Calendar hours section, in editing order.
    /// </summary>
    public static IReadOnlyList<CustomThemeColorKey> CalendarKeys { get; } = (CustomThemeColorKey[])
    [
        CustomThemeColorKey.CalendarDefaultHour,
        CustomThemeColorKey.CalendarWorkHour,
        CustomThemeColorKey.CalendarHoverHour,
        CustomThemeColorKey.CalendarSelectedHour
    ];

    public static CustomThemeColorGroup GetGroup(CustomThemeColorKey key) => key switch
    {
        CustomThemeColorKey.BaseSurface => CustomThemeColorGroup.Base,
        CustomThemeColorKey.CalendarDefaultHour or
        CustomThemeColorKey.CalendarHoverHour or
        CustomThemeColorKey.CalendarWorkHour or
        CustomThemeColorKey.CalendarSelectedHour => CustomThemeColorGroup.Calendar,
        _ => CustomThemeColorGroup.Surface
    };

    public static ThemeSurfaceScene GetScene(CustomThemeColorKey key) => key switch
    {
        CustomThemeColorKey.BaseSurface => ThemeSurfaceScene.Window,
        CustomThemeColorKey.Navigation => ThemeSurfaceScene.Navigation,
        CustomThemeColorKey.Workspace => ThemeSurfaceScene.Workspace,
        CustomThemeColorKey.MailListHeader => ThemeSurfaceScene.MailListHeader,
        CustomThemeColorKey.ReadingPane => ThemeSurfaceScene.ReadingPane,
        _ => ThemeSurfaceScene.CalendarGrid
    };

    /// <summary>
    /// True when readable body text sits directly on this surface, so its contrast is worth reporting.
    /// </summary>
    public static bool CarriesBodyText(CustomThemeColorKey key)
        => key is CustomThemeColorKey.Workspace or CustomThemeColorKey.MailListHeader or CustomThemeColorKey.ReadingPane;

    public static string GetLabel(CustomThemeColorKey key) => key switch
    {
        CustomThemeColorKey.BaseSurface => Translator.ApplicationThemeEditor_BaseSurface,
        CustomThemeColorKey.MailListHeader => Translator.ApplicationThemeEditor_MailHeader,
        CustomThemeColorKey.Workspace => Translator.ApplicationThemeEditor_WorkspaceSurface,
        CustomThemeColorKey.Navigation => Translator.ApplicationThemeEditor_NavigationSurface,
        CustomThemeColorKey.ReadingPane => Translator.ApplicationThemeEditor_ReadingSurface,
        CustomThemeColorKey.CalendarDefaultHour => Translator.ApplicationThemeEditor_CalendarDefault,
        CustomThemeColorKey.CalendarHoverHour => Translator.ApplicationThemeEditor_CalendarHover,
        CustomThemeColorKey.CalendarWorkHour => Translator.ApplicationThemeEditor_CalendarWork,
        CustomThemeColorKey.CalendarSelectedHour => Translator.ApplicationThemeEditor_CalendarSelected,
        _ => string.Empty
    };

    public static string GetDescription(CustomThemeColorKey key) => key switch
    {
        CustomThemeColorKey.BaseSurface => Translator.ApplicationThemeEditor_BaseSurfaceWhere,
        CustomThemeColorKey.MailListHeader => Translator.ApplicationThemeEditor_MailHeaderWhere,
        CustomThemeColorKey.Workspace => Translator.ApplicationThemeEditor_WorkspaceWhere,
        CustomThemeColorKey.Navigation => Translator.ApplicationThemeEditor_NavigationWhere,
        CustomThemeColorKey.ReadingPane => Translator.ApplicationThemeEditor_ReadingWhere,
        CustomThemeColorKey.CalendarDefaultHour => Translator.ApplicationThemeEditor_CalendarDefaultWhere,
        CustomThemeColorKey.CalendarHoverHour => Translator.ApplicationThemeEditor_CalendarHoverWhere,
        CustomThemeColorKey.CalendarWorkHour => Translator.ApplicationThemeEditor_CalendarWorkWhere,
        CustomThemeColorKey.CalendarSelectedHour => Translator.ApplicationThemeEditor_CalendarSelectedWhere,
        _ => string.Empty
    };
}
