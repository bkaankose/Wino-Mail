using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;

namespace Wino.Core.ViewModels;

public partial class AboutPageViewModel : CoreBaseViewModel
{
    private readonly IStoreRatingService _storeRatingService;
    private readonly IMailDialogService _dialogService;
    private readonly INativeAppService _nativeAppService;
    private readonly IApplicationConfiguration _appInitializerService;
    private readonly IClipboardService _clipboardService;
    private readonly IFileService _fileService;
    private readonly IWinoLogger _logInitializer;

    public string VersionName => _nativeAppService.GetFullAppVersion();
    public string WebsiteUrl => AppUrls.Website;
    public string DiscordChannelUrl => AppUrls.Discord;
    public string GitHubUrl => AppUrls.GitHub;
    public string PrivacyPolicyUrl => AppUrls.PrivacyPolicy;
    public string PaypalUrl => AppUrls.Paypal;

    public IPreferencesService PreferencesService { get; }

    public AboutPageViewModel(IStoreRatingService storeRatingService,
                              IMailDialogService dialogService,
                              INativeAppService nativeAppService,
                              IPreferencesService preferencesService,
                              IApplicationConfiguration appInitializerService,
                              IClipboardService clipboardService,
                              IFileService fileService,
                              IWinoLogger logInitializer)
    {
        _storeRatingService = storeRatingService;
        _dialogService = dialogService;
        _nativeAppService = nativeAppService;
        _logInitializer = logInitializer;
        _appInitializerService = appInitializerService;
        _clipboardService = clipboardService;
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
    private async Task CopyDiagnosticId()
    {
        try
        {
            await _clipboardService.CopyClipboardAsync(PreferencesService.DiagnosticId);
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
                                                                     archiveFileName);

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

    private Task ShowRateDialogAsync() => _storeRatingService.LaunchStorePageForReviewAsync();

    private static string GetSafeFileName(string fileName)
    {
        foreach (var invalidChar in Path.GetInvalidFileNameChars())
        {
            fileName = fileName.Replace(invalidChar, '_');
        }

        return fileName;
    }
}
