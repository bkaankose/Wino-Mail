#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Serilog;
using Wino.Core.Diagnostics;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.ViewModels.Common;
using Wino.Services.Interfaces;

namespace Wino.Mail.ViewModels.AccountSettings;

/// <summary>Protocol logging of one account, and saving or sending its logs.</summary>
public partial class AccountTroubleshootingPageViewModel : CoreBaseViewModel
{
    private readonly IMailDialogService _dialogService;
    private readonly IAccountService _accountService;
    private readonly ISynchronizationManager _synchronizationManager;
    private readonly IApplicationConfiguration _applicationConfiguration;
    private readonly IFileService _fileService;
    private readonly IPreferencesService _preferencesService;
    private readonly IWinoLogger _winoLogger;
    private bool _isLoading;

    public AccountTroubleshootingPageViewModel(
        IMessenger messenger,
        IMailDialogService dialogService,
        IAccountService accountService,
        ISynchronizationManager synchronizationManager,
        IApplicationConfiguration applicationConfiguration,
        IFileService fileService,
        IPreferencesService preferencesService,
        IWinoLogger winoLogger) : base(messenger)
    {
        _dialogService = dialogService;
        _accountService = accountService;
        _synchronizationManager = synchronizationManager;
        _applicationConfiguration = applicationConfiguration;
        _fileService = fileService;
        _preferencesService = preferencesService;
        _winoLogger = winoLogger;
    }

    [ObservableProperty]
    public partial MailAccount? Account { get; set; }

    [ObservableProperty]
    public partial bool IsProtocolLogEnabled { get; set; }

    /// <summary>Whether this account has written protocol logs yet; saving and sending need some.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNoLogsNoteVisible))]
    [NotifyCanExecuteChangedFor(nameof(ExportProtocolLogsCommand))]
    [NotifyCanExecuteChangedFor(nameof(UploadProtocolLogsCommand))]
    public partial bool HasLogs { get; set; }

    public bool IsNoLogsNoteVisible => Account != null && !HasLogs;

    public override async void OnNavigatedTo(NavigationMode mode, object? parameters)
    {
        base.OnNavigatedTo(mode, parameters);

        if (parameters is not Guid accountId)
            return;

        var account = await _accountService.GetAccountAsync(accountId);
        if (account == null)
            return;

        var hasLogs = HasLogFiles(account.Id);

        await ExecuteUIThread(() =>
        {
            _isLoading = true;
            Account = account;
            IsProtocolLogEnabled = account.IsProtocolLogEnabled;
            HasLogs = hasLogs;
            OnPropertyChanged(nameof(IsNoLogsNoteVisible));
            _isLoading = false;
        });
    }

    private bool HasLogFiles(Guid accountId)
    {
        var folder = WinoProtocolLogger.GetAccountLogFolder(_applicationConfiguration.ApplicationDataFolderPath, accountId);

        try
        {
            return Directory.Exists(folder) && Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Any();
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    partial void OnIsProtocolLogEnabledChanged(bool value)
    {
        if (!_isLoading && Account != null)
            _ = SaveProtocolLogAsync(Account, value);
    }

    private async Task SaveProtocolLogAsync(MailAccount account, bool isEnabled)
    {
        try
        {
            account.IsProtocolLogEnabled = isEnabled;
            await _accountService.UpdateAccountAsync(account);

            // The protocol logger is chosen when the synchronizer and its connection pool are created.
            await _synchronizationManager.DestroySynchronizerAsync(account.Id);
        }
        catch (Exception ex)
        {
            _dialogService.InfoBarMessage(Translator.GeneralTitle_Error, ex.Message, InfoBarMessageType.Error);
        }
    }

    private string? GetLogsFolder()
    {
        if (Account == null)
            return null;

        var folder = WinoProtocolLogger.GetAccountLogFolder(_applicationConfiguration.ApplicationDataFolderPath, Account.Id);
        if (Directory.Exists(folder))
            return folder;

        _dialogService.InfoBarMessage(Translator.Info_LogsNotFoundTitle, Translator.ProtocolLog_NoLogsMessage, InfoBarMessageType.Error);
        return null;
    }

    [RelayCommand(CanExecute = nameof(HasLogs))]
    private async Task ExportProtocolLogsAsync()
    {
        var logsFolder = GetLogsFolder();
        if (logsFolder == null || Account == null)
            return;

        var selectedFolderPath = await _dialogService.PickFolderAsync();
        if (string.IsNullOrEmpty(selectedFolderPath))
            return;

        try
        {
            var archiveFileName = $"Wino-Protocol-{Account.Id:N}-{DateTime.Now:yyyyMMdd-HHmmss}.zip";
            var archivePath = await _fileService.CreateLogsArchiveAsync(logsFolder, selectedFolderPath, archiveFileName);

            if (string.IsNullOrEmpty(archivePath))
            {
                _dialogService.InfoBarMessage(Translator.Info_LogsNotFoundTitle, Translator.ProtocolLog_NoLogsMessage, InfoBarMessageType.Error);
                return;
            }

            _dialogService.InfoBarMessage(
                Translator.ProtocolLog_ArchiveSavedTitle,
                string.Format(Translator.ProtocolLog_ArchiveSavedMessage, archiveFileName),
                InfoBarMessageType.Success);
        }
        catch (Exception ex)
        {
            _dialogService.InfoBarMessage(Translator.GeneralTitle_Error, ex.Message, InfoBarMessageType.Error);
        }
    }

    [RelayCommand(CanExecute = nameof(HasLogs))]
    private async Task UploadProtocolLogsAsync()
    {
        var logsFolder = GetLogsFolder();
        if (logsFolder == null || Account == null)
            return;

        var archiveFileName = $"Wino-Protocol-{Account.Id:N}.zip";
        var archivePath = await _fileService.CreateLogsArchiveAsync(
            logsFolder,
            _applicationConfiguration.ApplicationTempFolderPath,
            archiveFileName,
            sanitizeSensitiveData: true);

        if (string.IsNullOrEmpty(archivePath))
        {
            _dialogService.InfoBarMessage(Translator.Info_LogsNotFoundTitle, Translator.ProtocolLog_NoLogsMessage, InfoBarMessageType.Error);
            return;
        }

        try
        {
            await _winoLogger.UploadDiagnosticLogsAsync(archivePath, _preferencesService.DiagnosticId);
            _dialogService.InfoBarMessage(
                Translator.Info_LogsUploadedTitle,
                string.Format(Translator.Info_LogsUploadedMessage, archiveFileName),
                InfoBarMessageType.Success);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to upload protocol logs.");
            _dialogService.InfoBarMessage(Translator.GeneralTitle_Error, Translator.Info_LogsUploadFailedMessage, InfoBarMessageType.Error);
        }
    }
}
