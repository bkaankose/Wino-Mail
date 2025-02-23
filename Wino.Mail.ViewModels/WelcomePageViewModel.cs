using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;

namespace Wino.Mail.ViewModels;

public partial class WelcomePageViewModel : MailBaseViewModel
{
    public const string VersionFile = "1102.md";
    private readonly IMailDialogService _dialogService;
    private readonly IFileService _fileService;

    [ObservableProperty]
    private string currentVersionNotes;

    public WelcomePageViewModel(IMailDialogService dialogService, IFileService fileService)
    {
        _dialogService = dialogService;
        _fileService = fileService;
    }

    public override async void OnNavigatedTo(NavigationMode mode, object parameters)
    {
        base.OnNavigatedTo(mode, parameters);

        try
        {
            CurrentVersionNotes = await _fileService.GetFileContentByApplicationUriAsync($"ms-appx:///Assets/ReleaseNotes/{VersionFile}");
        }
        catch (Exception)
        {
            _dialogService.InfoBarMessage(Translator.GeneralTitle_Error, "Can't find the patch notes.", Core.Domain.Enums.InfoBarMessageType.Information);
        }
    }
}
