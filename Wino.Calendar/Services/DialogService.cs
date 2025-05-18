using Windows.UI.Xaml;
using Wino.Core.Domain.Interfaces;
using Wino.Core.UWP.Services;

namespace Wino.Calendar.Services;

public class DialogService : DialogServiceBase, ICalendarDialogService
{
    public DialogService(IThemeService themeService,
                         IConfigurationService configurationService,
                         IApplicationResourceManager<ResourceDictionary> applicationResourceManager) : base(themeService, configurationService, applicationResourceManager)
    {
    }
}
