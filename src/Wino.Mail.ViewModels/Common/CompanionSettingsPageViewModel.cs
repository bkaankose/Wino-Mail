using Wino.Core.Domain.Interfaces;

namespace Wino.Core.ViewModels;

public partial class CompanionSettingsPageViewModel(IPreferencesService preferencesService) : CoreBaseViewModel
{
    public IPreferencesService PreferencesService { get; } = preferencesService;
}
