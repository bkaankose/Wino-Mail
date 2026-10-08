using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Messaging.UI;

namespace Wino.Core.ViewModels;

public partial class AboutPageViewModel : CoreBaseViewModel
{
    private readonly IMicrosoftStoreService _storeService;
    private readonly IMailDialogService _dialogService;
    private readonly INativeAppService _nativeAppService;
    private readonly IAppMetadataService _appMetadataService;
    private readonly IApplicationConfiguration _appInitializerService;
    private readonly IFileService _fileService;
    private readonly IWinoLogger _logInitializer;

    public string VersionName => _appMetadataService.AppVersion;
    public string WebsiteUrl => AppUrls.Website;
    public string DiscordChannelUrl => AppUrls.Discord;
    public string GitHubUrl => AppUrls.GitHub;
    public string PrivacyPolicyUrl => AppUrls.PrivacyPolicy;
    public string PaypalUrl => AppUrls.Paypal;

    public IPreferencesService PreferencesService { get; }

    public AboutPageViewModel(IMicrosoftStoreService storeService,
                              IMailDialogService dialogService,
                              INativeAppService nativeAppService,
                              IAppMetadataService appMetadataService,
                              IPreferencesService preferencesService,
                              IApplicationConfiguration appInitializerService,
                              IFileService fileService,
                              IWinoLogger logInitializer)
    {
        _storeService = storeService;
        _dialogService = dialogService;
        _nativeAppService = nativeAppService;
        _appMetadataService = appMetadataService;
        _logInitializer = logInitializer;
        _appInitializerService = appInitializerService;
        _fileService = fileService;

        PreferencesService = preferencesService;
    }

    public override void OnNavigatedTo(NavigationMode mode, object parameters)
    {
        base.OnNavigatedTo(mode, parameters);

        PreferencesService.PreferenceChanged -= PreferencesChanged;
        PreferencesService.PreferenceChanged += PreferencesChanged;
    }

    public override void OnNavigatedFrom(NavigationMode mode, object parameters)
    {
        base.OnNavigatedFrom(mode, parameters);

        PreferencesService.PreferenceChanged -= PreferencesChanged;
    }

    private void PreferencesChanged(object sender, string e)
    {
        if (e == nameof(PreferencesService.IsLoggingEnabled))
        {
            _logInitializer.RefreshLoggingLevel();
        }
    }

    [RelayCommand]
    private void OpenWhatsNew() => WeakReferenceMessenger.Default.Send(new WhatsNewOpenRequested());

    [RelayCommand]
    private async Task CopyDiagnosticId()
    {
        try
        {
            await _nativeAppService.CopyClipboardAsync(PreferencesService.DiagnosticId);
            _dialogService.InfoBarMessage(Translator.Buttons_Copy, string.Format(Translator.ClipboardTextCopied_Message, "Id"), InfoBarMessageType.Success);
        }
        catch (Exception ex)
        {
            _dialogService.InfoBarMessage(Translator.GeneralTitle_Error, string.Format(Translator.ClipboardTextCopyFailed_Message, "Id"), InfoBarMessageType.Error);
            Log.Error(ex, "Failed to copy diagnostic id to clipboard.");
        }
    }

    [RelayCommand]
    private async Task ShareWinoLogAsync()
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
    private async Task UploadWinoLogsAsync()
    {
        var diagnosticId = PreferencesService.DiagnosticId;
        var archiveFileName = $"{GetSafeFileName(diagnosticId)}.zip";
        var archivePath = await _fileService.CreateLogsArchiveAsync(_appInitializerService.ApplicationDataFolderPath,
                                                                     _appInitializerService.ApplicationTempFolderPath,
                                                                     archiveFileName,
                                                                     sanitizeSensitiveData: true);

        if (string.IsNullOrEmpty(archivePath))
        {
            _dialogService.InfoBarMessage(Translator.Info_LogsNotFoundTitle, Translator.Info_LogsNotFoundMessage, InfoBarMessageType.Error);
            return;
        }

        try
        {
            await _logInitializer.UploadDiagnosticLogsAsync(archivePath, diagnosticId);
            _dialogService.InfoBarMessage(Translator.Info_LogsUploadedTitle, string.Format(Translator.Info_LogsUploadedMessage, archiveFileName), InfoBarMessageType.Success);
        }
        catch (Exception ex)
        {
            _dialogService.InfoBarMessage(Translator.GeneralTitle_Error, Translator.Info_LogsUploadFailedMessage, InfoBarMessageType.Error);
            Log.Error(ex, "Failed to upload diagnostic logs to Sentry.");
        }
    }

    [RelayCommand]
    private async Task Navigate(object url)
    {
        if (url is string stringUrl)
        {
            if (stringUrl == "Store")
                await ShowRateDialogAsync();
            else
            {
                // Discord disclaimer message about server.
                if (stringUrl == DiscordChannelUrl)
                    await _dialogService.ShowMessageAsync(Translator.DiscordChannelDisclaimerMessage,
                                                         Translator.DiscordChannelDisclaimerTitle,
                                                         WinoCustomMessageDialogIcon.Warning);

                await _nativeAppService.LaunchUriAsync(new Uri(stringUrl));
            }
        }
    }

    private Task ShowRateDialogAsync() => _storeService.LaunchStorePageForReviewAsync();

    private static string GetSafeFileName(string fileName)
    {
        foreach (var invalidChar in Path.GetInvalidFileNameChars())
        {
            fileName = fileName.Replace(invalidChar, '_');
        }

        return fileName;
    }
}
