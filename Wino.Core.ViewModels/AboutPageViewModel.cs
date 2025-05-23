﻿using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

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
    public string DiscordChannelUrl => "https://discord.gg/windows-apps-hub-714581497222398064";
    public string GitHubUrl => "https://github.com/bkaankose/Wino-Mail/";
    public string PrivacyPolicyUrl => "https://www.winomail.app/support/privacy";
    public string PaypalUrl => "https://paypal.me/bkaankose?country.x=PL&locale.x=en_US";

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

    [RequiresDynamicCode("AOT")]
    [RequiresUnreferencedCode("AOT")]
    protected override void OnActivated()
    {
        base.OnActivated();

        PreferencesService.PreferenceChanged -= PreferencesChanged;
        PreferencesService.PreferenceChanged += PreferencesChanged;
    }

    protected override void OnDeactivated()
    {
        base.OnDeactivated();

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

        var areLogsSaved = await _fileService.SaveLogsToFolderAsync(appDataFolder, selectedFolderPath).ConfigureAwait(false);

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
}
