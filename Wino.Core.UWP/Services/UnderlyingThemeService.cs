using Windows.UI.ViewManagement;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.UWP.Services;

public class UnderlyingThemeService : IUnderlyingThemeService
{
    public const string SelectedAppThemeKey = nameof(SelectedAppThemeKey);

    private readonly UISettings uiSettings = new UISettings();
    private readonly IConfigurationService _configurationService;

    public UnderlyingThemeService(IConfigurationService configurationService)
    {
        _configurationService = configurationService;
    }

    // This should not rely on application window to be present.
    // Check theme from the settings, rely on UISettings background color if Default.

    public bool IsUnderlyingThemeDark()
    {
        var currentTheme = _configurationService.Get(SelectedAppThemeKey, ApplicationElementTheme.Default);

        if (currentTheme == ApplicationElementTheme.Default)
            return uiSettings.GetColorValue(UIColorType.Background).ToString() == "#FF000000";
        else
            return currentTheme == ApplicationElementTheme.Dark;
    }
}
