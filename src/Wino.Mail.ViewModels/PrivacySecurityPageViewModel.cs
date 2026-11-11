#nullable enable
using System;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.Settings;
using Wino.Core.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Messaging.Client.Navigation;
using Wino.Messaging.Client.Shell;
using Wino.Messaging.UI;

namespace Wino.Mail.ViewModels;

/// <summary>
/// Settings -> General -> Privacy &amp; security: the remote content mode and its exceptions, link and attachment
/// safeguards, and what Wino itself shares.
/// </summary>
public partial class PrivacySecurityPageViewModel : CoreBaseViewModel,
    IRecipient<PreferenceChangedMessage>,
    IRecipient<RemoteContentRulesChangedMessage>
{
    private readonly IRemoteContentRuleStore _ruleStore;
    private readonly IMailDialogService _dialogService;
    private readonly IExternalLauncher _externalLauncher;
    private readonly IFileService _fileService;
    private readonly IApplicationConfiguration _applicationConfiguration;
    private readonly IWinoLogger _logger;
    private readonly IPlatformCapabilities _platformCapabilities;
    private readonly ILinkBrowserService _linkBrowserService;
    private readonly IAccountService _accountService;

    public IPreferencesService PreferencesService { get; }

    /// <summary>The three mode cards in display order; exactly one has <see cref="RemoteContentModeOption.IsSelected"/>.</summary>
    public RemoteContentModeOption[] ModeOptions { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoadEverything), nameof(IsBlockTrackers), nameof(IsBlockAll))]
    [NotifyPropertyChangedFor(nameof(HeroTitle), nameof(HeroDescription), nameof(HeroModeStatus))]
    public partial RemoteContentMode SelectedMode { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RuleCountText), nameof(HeroRuleStatus))]
    public partial int RuleCount { get; set; }

    public bool IsLoadEverything
    {
        get => SelectedMode == RemoteContentMode.LoadEverything;
        set { if (value) SelectedMode = RemoteContentMode.LoadEverything; }
    }

    public bool IsBlockTrackers
    {
        get => SelectedMode == RemoteContentMode.BlockTrackers;
        set { if (value) SelectedMode = RemoteContentMode.BlockTrackers; }
    }

    public bool IsBlockAll
    {
        get => SelectedMode == RemoteContentMode.BlockAll;
        set { if (value) SelectedMode = RemoteContentMode.BlockAll; }
    }

    public string HeroTitle => SelectedMode switch
    {
        RemoteContentMode.LoadEverything => Translator.PrivacySecurity_HeroLoadEverything_Title,
        RemoteContentMode.BlockAll => Translator.PrivacySecurity_HeroBlockAll_Title,
        _ => Translator.PrivacySecurity_HeroBlockTrackers_Title
    };

    public string HeroDescription => SelectedMode switch
    {
        RemoteContentMode.LoadEverything => Translator.PrivacySecurity_HeroLoadEverything_Description,
        RemoteContentMode.BlockAll => Translator.PrivacySecurity_HeroBlockAll_Description,
        _ when PreferencesService.StripLinkTrackingParameters => Translator.PrivacySecurity_HeroBlockTrackersLinks_Description,
        _ => Translator.PrivacySecurity_HeroBlockTrackers_Description
    };

    public string HeroModeStatus => SelectedMode switch
    {
        RemoteContentMode.LoadEverything => Translator.PrivacySecurity_HeroStatusLoadEverything,
        RemoteContentMode.BlockAll => Translator.PrivacySecurity_HeroStatusBlockAll,
        _ => Translator.PrivacySecurity_HeroStatusBlockTrackers
    };

    public string HeroRuleStatus => RuleCount switch
    {
        0 => Translator.PrivacySecurity_HeroStatusNoRules,
        1 => Translator.PrivacySecurity_HeroStatusOneRule,
        _ => string.Format(Translator.PrivacySecurity_HeroStatusRuleCount, RuleCount)
    };

    public string HeroLinkStatus => PreferencesService.StripLinkTrackingParameters
        ? Translator.PrivacySecurity_HeroStatusLinksCleaned
        : Translator.PrivacySecurity_HeroStatusLinksUnchanged;

    public string RuleCountText => RuleCount switch
    {
        0 => Translator.PrivacySecurity_SenderRules_CountNone,
        1 => Translator.PrivacySecurity_SenderRules_CountOne,
        _ => string.Format(Translator.PrivacySecurity_SenderRules_Count, RuleCount)
    };

    /// <summary>The Windows Security card; only Windows checks attachments through the operating system.</summary>
    public bool IsAttachmentSecurityCheckAvailable => _platformCapabilities.AttachmentSecurityCheck;

    public bool IsNotificationsLinkAvailable => SettingsCatalog.IsVisible(WinoPage.NotificationSettingsPage);

    public string NotificationContentText => PreferencesService.MailNotificationContent switch
    {
        MailNotificationContent.SenderSubject => Translator.MailNotificationContent_SenderSubject,
        MailNotificationContent.SenderOnly => Translator.MailNotificationContent_SenderOnly,
        MailNotificationContent.Nothing => Translator.MailNotificationContent_Nothing,
        _ => Translator.MailNotificationContent_SenderSubjectPreview
    };

    public bool IsSignaturesLinkAvailable => SettingsCatalog.IsVisible(WinoPage.SignatureAndEncryptionPage);

    public bool IsIntelligenceLinkAvailable => SettingsCatalog.IsVisible(WinoPage.WinoIntelligencePage);

    public PrivacySecurityPageViewModel(IPreferencesService preferencesService,
                                        IRemoteContentRuleStore ruleStore,
                                        IMailDialogService dialogService,
                                        IExternalLauncher externalLauncher,
                                        IFileService fileService,
                                        IApplicationConfiguration applicationConfiguration,
                                        IWinoLogger logger,
                                        IPlatformCapabilities platformCapabilities,
                                        ILinkBrowserService linkBrowserService,
                                        IAccountService accountService)
    {
        PreferencesService = preferencesService;
        _ruleStore = ruleStore;
        _dialogService = dialogService;
        _externalLauncher = externalLauncher;
        _fileService = fileService;
        _applicationConfiguration = applicationConfiguration;
        _logger = logger;
        _platformCapabilities = platformCapabilities;
        _linkBrowserService = linkBrowserService;
        _accountService = accountService;

        ModeOptions = RemoteContentModeOption.CreateAll(mode => SelectedMode = mode);
        SelectedMode = preferencesService.RemoteContentMode;
        SyncModeOptions();
    }

    public override void OnNavigatedTo(NavigationMode mode, object? parameters)
    {
        base.OnNavigatedTo(mode, parameters);

        SelectedMode = PreferencesService.RemoteContentMode;
        OnPropertyChanged(nameof(NotificationContentText));

        _ = LoadRuleCountAsync();
        _ = RefreshLinkBrowserSettingsAsync();
    }

    protected override void RegisterRecipients()
    {
        base.RegisterRecipients();

        Messenger.Register<PreferenceChangedMessage>(this);
        Messenger.Register<RemoteContentRulesChangedMessage>(this);
    }

    protected override void UnregisterRecipients()
    {
        base.UnregisterRecipients();

        Messenger.Unregister<PreferenceChangedMessage>(this);
        Messenger.Unregister<RemoteContentRulesChangedMessage>(this);
    }

    partial void OnSelectedModeChanged(RemoteContentMode value)
    {
        if (PreferencesService.RemoteContentMode != value)
            PreferencesService.RemoteContentMode = value;

        SyncModeOptions();
    }

    private void SyncModeOptions()
    {
        foreach (var option in ModeOptions)
        {
            option.IsSelected = option.Mode == SelectedMode;
        }
    }

    public void Receive(PreferenceChangedMessage message)
    {
        switch (message.PropertyName)
        {
            case nameof(IPreferencesService.IsLoggingEnabled):
                _logger.RefreshLoggingLevel();
                break;
            case nameof(IPreferencesService.RemoteContentMode):
                _ = ExecuteUIThread(() => SelectedMode = PreferencesService.RemoteContentMode);
                break;
            case nameof(IPreferencesService.StripLinkTrackingParameters):
                _ = ExecuteUIThread(() =>
                {
                    OnPropertyChanged(nameof(HeroDescription));
                    OnPropertyChanged(nameof(HeroLinkStatus));
                });
                break;
            case nameof(IPreferencesService.MailNotificationContent):
                _ = ExecuteUIThread(() => OnPropertyChanged(nameof(NotificationContentText)));
                break;
        }
    }

    public void Receive(RemoteContentRulesChangedMessage message) => _ = LoadRuleCountAsync();

    [RelayCommand]
    private void OpenSenderRules() => Navigate(WinoPage.RemoteContentRulesPage);

    [RelayCommand]
    private void OpenNotificationSettings() => Navigate(WinoPage.NotificationSettingsPage);

    [RelayCommand]
    private void OpenSignatureAndEncryption() => Navigate(WinoPage.SignatureAndEncryptionPage);

    [RelayCommand]
    private void OpenWinoIntelligence() => Navigate(WinoPage.WinoIntelligencePage);

    [RelayCommand]
    private void OpenWinoIntelligencePrivacy()
        => Messenger.Send(new SettingsRootNavigationRequested(WinoPage.WinoIntelligencePage));

    /// <summary>
    /// Shows the log folder where the platform has a file manager; elsewhere saves the logs to a folder the user picks,
    /// the same export as About's Share button.
    /// </summary>
    [RelayCommand]
    private async Task OpenLogFolderAsync()
    {
        var dataFolder = _applicationConfiguration.ApplicationDataFolderPath;
        var logsSubfolder = Path.Combine(dataFolder, "Logs");
        var logFolder = Directory.Exists(logsSubfolder) ? logsSubfolder : dataFolder;

        var result = await _externalLauncher.LaunchFolderAsync(logFolder);

        if (result.IsSuccess)
            return;

        if (result.Status != PlatformOperationStatus.Unavailable)
        {
            Log.Warning("Could not open the log folder: {Error}", result.ErrorMessage);
            _dialogService.InfoBarMessage(Translator.GeneralTitle_Error, result.ErrorMessage ?? string.Empty, InfoBarMessageType.Error);
            return;
        }

        var selectedFolderPath = await _dialogService.PickFolderAsync();

        if (string.IsNullOrEmpty(selectedFolderPath))
            return;

        if (await _fileService.SaveLogsToFolderAsync(logFolder, selectedFolderPath))
            _dialogService.InfoBarMessage(Translator.Info_LogsSavedTitle, string.Format(Translator.Info_LogsSavedMessage, Constants.LogArchiveFileName), InfoBarMessageType.Success);
        else
            _dialogService.InfoBarMessage(Translator.Info_LogsNotFoundTitle, Translator.Info_LogsNotFoundMessage, InfoBarMessageType.Error);
    }

    private void Navigate(WinoPage page)
        => Messenger.Send(new BreadcrumbNavigationRequested(SettingsCatalog.Title(page), page));

    private async Task LoadRuleCountAsync()
    {
        try
        {
            var count = await _ruleStore.GetCountAsync();
            await ExecuteUIThread(() => RuleCount = count);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to count remote content sender rules.");
        }
    }
}
