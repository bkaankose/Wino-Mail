using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using MailCal.Core.Domain;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Interfaces;
using MailCal.Messaging.UI;
using MailCal.Messaging.Client.Shell;

namespace MailCal.Core.ViewModels;

public partial class AboutPageViewModel : CoreBaseViewModel,
    IRecipient<PreferenceChangedMessage>
{
    private readonly IMailDialogService _dialogService;
    private readonly IExternalLauncher _externalLauncher;
    private readonly IAppMetadataService _appMetadataService;
    private readonly IApplicationConfiguration _appInitializerService;
    private readonly IFileService _fileService;
    private readonly IMailCalLogger _logInitializer;

    public string VersionName => _appMetadataService.AppVersion;
    public string GitHubUrl => AppUrls.GitHub;
    public string UpstreamUrl => AppUrls.Upstream;

    public IPreferencesService PreferencesService { get; }

    public AboutPageViewModel(IMailDialogService dialogService,
                              IExternalLauncher externalLauncher,
                              IAppMetadataService appMetadataService,
                              IPreferencesService preferencesService,
                              IApplicationConfiguration appInitializerService,
                              IFileService fileService,
                              IMailCalLogger logInitializer)
    {
        _dialogService = dialogService;
        _externalLauncher = externalLauncher;
        _appMetadataService = appMetadataService;
        _logInitializer = logInitializer;
        _appInitializerService = appInitializerService;
        _fileService = fileService;

        PreferencesService = preferencesService;
    }

    protected override void RegisterRecipients()
    {
        base.RegisterRecipients();

        Messenger.Register<PreferenceChangedMessage>(this);
    }

    protected override void UnregisterRecipients()
    {
        base.UnregisterRecipients();

        Messenger.Unregister<PreferenceChangedMessage>(this);
    }

    public void Receive(PreferenceChangedMessage message)
    {
        if (message.PropertyName == nameof(IPreferencesService.IsLoggingEnabled))
        {
            _logInitializer.RefreshLoggingLevel();
        }
    }

    [RelayCommand]
    private void OpenWhatsNew() => WeakReferenceMessenger.Default.Send(new WhatsNewOpenRequested());

    [RelayCommand]
    private async Task ShareMailCalLogAsync()
    {
        var appDataFolder = _appInitializerService.ApplicationDataFolderPath;

        var selectedFolderPath = await _dialogService.PickWindowsFolderAsync();

        if (string.IsNullOrEmpty(selectedFolderPath)) return;

        var areLogsSaved = await _fileService.SaveLogsToFolderAsync(appDataFolder, selectedFolderPath);

        if (areLogsSaved)
        {
            _dialogService.InfoBarMessage(Translator.Info_LogsSavedTitle, string.Format(Translator.Info_LogsSavedMessage, Constants.LogArchiveFileName), InfoBarMessageType.Success);
        }
        else
        {
            _dialogService.InfoBarMessage(Translator.Info_LogsNotFoundTitle, Translator.Info_LogsNotFoundMessage, InfoBarMessageType.Error);
        }
    }

    [RelayCommand]
    private async Task Navigate(object url)
    {
        if (url is string stringUrl)
            (await _externalLauncher.LaunchUriAsync(new Uri(stringUrl))).ThrowIfNotSucceeded();
    }
}
