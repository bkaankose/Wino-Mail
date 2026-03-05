using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.Updates;
using Wino.Messaging.Client.Navigation;

namespace Wino.Mail.ViewModels;

public partial class WelcomePageV2ViewModel : MailBaseViewModel
{
    private readonly IUpdateManager _updateManager;

    [ObservableProperty]
    public partial List<UpdateNoteSection> UpdateSections { get; set; } = [];

    public WelcomePageV2ViewModel(IUpdateManager updateManager)
    {
        _updateManager = updateManager;
    }

    public override async void OnNavigatedTo(NavigationMode mode, object parameters)
    {
        base.OnNavigatedTo(mode, parameters);

        try
        {
            var updateNotes = await _updateManager.GetLatestUpdateNotesAsync();
            UpdateSections = updateNotes.Sections;
        }
        catch (Exception)
        {
            UpdateSections = [];
        }
    }

    [RelayCommand]
    private void GetStarted()
    {
        Messenger.Send(new GetStartedFromWelcomeRequested());
    }
}
