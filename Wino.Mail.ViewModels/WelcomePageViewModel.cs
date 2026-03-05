using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.Updates;

namespace Wino.Mail.ViewModels;

public partial class WelcomePageViewModel : MailBaseViewModel
{
    private readonly IUpdateManager _updateManager;
    private readonly INativeAppService _nativeAppService;
    private readonly INavigationService _navigationService;

    [ObservableProperty]
    public partial string VersionDisplay { get; set; } = string.Empty;

    [ObservableProperty]
    public partial List<UpdateNoteSection> UpdateSections { get; set; } = [];

    [ObservableProperty]
    public partial List<UpdateNoteSection> FeatureSections { get; set; } = [];

    public string GitHubUrl => "https://github.com/bkaankose/Wino-Mail/";
    public string PaypalUrl => "https://paypal.me/bkaankose?country.x=PL&locale.x=en_US";

    public WelcomePageViewModel(IUpdateManager updateManager,
                                INativeAppService nativeAppService,
                                INavigationService navigationService)
    {
        _updateManager = updateManager;
        _nativeAppService = nativeAppService;
        _navigationService = navigationService;
    }

    public override async void OnNavigatedTo(NavigationMode mode, object parameters)
    {
        base.OnNavigatedTo(mode, parameters);

        VersionDisplay = $"{Translator.SettingsAboutVersion}{_nativeAppService.GetFullAppVersion()}";

        try
        {
            var updateNotes = await _updateManager.GetLatestUpdateNotesAsync();
            UpdateSections = updateNotes.Sections;
        }
        catch (Exception)
        {
            UpdateSections = [];
        }

        try
        {
            FeatureSections = await _updateManager.GetFeaturesAsync();
        }
        catch (Exception)
        {
            FeatureSections = [];
        }
    }

    [RelayCommand]
    private void NavigateManageAccounts()
        => _navigationService.Navigate(WinoPage.ManageAccountsPage, null, NavigationReferenceFrame.ShellFrame, NavigationTransitionType.DrillIn);
}
