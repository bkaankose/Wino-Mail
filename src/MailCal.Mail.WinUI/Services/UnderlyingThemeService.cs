using Windows.UI.ViewManagement;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Interfaces;

namespace MailCal.Mail.WinUI.Services;

public class UnderlyingThemeService : IUnderlyingThemeService
{
    public const string SelectedAppThemeKey = nameof(SelectedAppThemeKey);

    private readonly UISettings uiSettings = new UISettings();
    private readonly IConfigurationService _configurationService;

    public UnderlyingThemeService(IConfigurationService configurationService)
    {
        _configurationService = configurationService;
    }

    public bool IsUnderlyingThemeDark()
    {
        var currentTheme = _configurationService.Get(SelectedAppThemeKey, ApplicationElementTheme.Default);

        if (currentTheme == ApplicationElementTheme.Default)
            return uiSettings.GetColorValue(UIColorType.Background).ToString() == "#FF000000";
        else
            return currentTheme == ApplicationElementTheme.Dark;
    }
}
