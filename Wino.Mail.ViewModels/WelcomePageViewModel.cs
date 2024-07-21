using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Domain;
using Wino.Domain.Enums;
using Wino.Domain.Interfaces;
using Wino.Domain.Models.Navigation;

namespace Wino.Mail.ViewModels
{
    public partial class WelcomePageViewModel : BaseViewModel
    {
        public const string VersionFile = "176.md";

        private readonly IFileService _fileService;

        [ObservableProperty]
        private string currentVersionNotes;

        public WelcomePageViewModel(IDialogService dialogService, IFileService fileService) : base(dialogService)
        {
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
                DialogService.InfoBarMessage(Translator.GeneralTitle_Error, "Can't find the patch notes.", InfoBarMessageType.Information);
            }
        }
    }
}
