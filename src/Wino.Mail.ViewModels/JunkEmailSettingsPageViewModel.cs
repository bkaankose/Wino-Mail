using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;

namespace Wino.Mail.ViewModels;

/// <summary>
/// Outlook-style "Junk email options" page: per-account Safe and Blocked sender lists with
/// add / remove and import / export to a text file. Lists are kept locally (the
/// <see cref="IJunkSenderService"/> store) and, where the provider keeps a server copy (Exchange over
/// MAPI/HTTP: the junk rule), each add / remove is pushed there too through <see cref="IServerJunkListService"/>;
/// a push that fails leaves the local edit and says so.
/// </summary>
public partial class JunkEmailSettingsPageViewModel : MailBaseViewModel
{
    private static readonly char[] ImportSeparators = ['\r', '\n', ',', ';', '\t', ' '];

    private readonly IJunkSenderService _junkSenderService;
    private readonly IServerJunkListService _serverJunkListService;
    private readonly IAccountService _accountService;
    private readonly IMailDialogService _dialogService;
    private readonly IFileService _fileService;

    public ObservableCollection<MailAccount> Accounts { get; } = [];

    [ObservableProperty]
    public partial MailAccount SelectedAccount { get; set; }

    [ObservableProperty]
    public partial string NewSafeSender { get; set; }

    [ObservableProperty]
    public partial string NewBlockedSender { get; set; }

    public ObservableCollection<JunkSender> SafeSenders { get; } = [];
    public ObservableCollection<JunkSender> BlockedSenders { get; } = [];

    public bool HasAccounts => Accounts.Count > 0;
    public bool HasSafeSenders => SafeSenders.Count > 0;
    public bool HasBlockedSenders => BlockedSenders.Count > 0;

    public JunkEmailSettingsPageViewModel(
        IJunkSenderService junkSenderService,
        IAccountService accountService,
        IMailDialogService dialogService,
        IFileService fileService,
        IServerJunkListService serverJunkListService)
    {
        _junkSenderService = junkSenderService;
        _serverJunkListService = serverJunkListService;
        _accountService = accountService;
        _dialogService = dialogService;
        _fileService = fileService;
    }

    public override async void OnNavigatedTo(NavigationMode mode, object parameters)
    {
        base.OnNavigatedTo(mode, parameters);

        var accounts = await _accountService.GetAccountsAsync().ConfigureAwait(false);

        await ExecuteUIThread(() =>
        {
            Accounts.Clear();
            foreach (var account in accounts)
                Accounts.Add(account);

            OnPropertyChanged(nameof(HasAccounts));

            // Deep-linked from an account context: preselect it; otherwise the first account.
            SelectedAccount = parameters is Guid accountId
                ? Accounts.FirstOrDefault(a => a.Id == accountId) ?? Accounts.FirstOrDefault()
                : Accounts.FirstOrDefault();
        }).ConfigureAwait(false);
    }

    partial void OnSelectedAccountChanged(MailAccount value) => _ = LoadListsAsync();

    [RelayCommand]
    private Task AddSafeSenderAsync() => AddSenderAsync(NewSafeSender, JunkListType.Safe);

    [RelayCommand]
    private Task AddBlockedSenderAsync() => AddSenderAsync(NewBlockedSender, JunkListType.Blocked);

    [RelayCommand]
    private Task ImportSafeAsync() => ImportAsync(JunkListType.Safe);

    [RelayCommand]
    private Task ImportBlockedAsync() => ImportAsync(JunkListType.Blocked);

    [RelayCommand]
    private Task ExportSafeAsync() => ExportAsync(JunkListType.Safe);

    [RelayCommand]
    private Task ExportBlockedAsync() => ExportAsync(JunkListType.Blocked);

    private async Task AddSenderAsync(string address, JunkListType listType)
    {
        if (SelectedAccount == null)
            return;

        if (!IsValidEntry(address))
        {
            await _dialogService.ShowMessageAsync(
                Translator.SettingsJunkEmail_InvalidAddress_Message,
                Translator.SettingsJunkEmail_InvalidAddress_Title,
                WinoCustomMessageDialogIcon.Warning).ConfigureAwait(false);
            return;
        }

        await _junkSenderService.AddSenderAsync(SelectedAccount.Id, address, listType).ConfigureAwait(false);

        await ExecuteUIThread(() =>
        {
            if (listType == JunkListType.Safe)
                NewSafeSender = string.Empty;
            else
                NewBlockedSender = string.Empty;
        }).ConfigureAwait(false);

        await LoadListsAsync().ConfigureAwait(false);
        await PushToServerAsync(address, listType, add: true).ConfigureAwait(false);
    }

    /// <summary>Mirrors the edit on the server's list when the account keeps one; a failure is reported, the local edit stands.</summary>
    private async Task PushToServerAsync(string address, JunkListType listType, bool add)
    {
        var account = SelectedAccount;
        if (account == null || !await _serverJunkListService.SupportsServerJunkListsAsync(account).ConfigureAwait(false))
            return;

        if (!await _serverJunkListService.TryUpdateAsync(account.Id, address, listType, add).ConfigureAwait(false))
        {
            await _dialogService.ShowMessageAsync(
                Translator.SettingsJunkEmail_ServerUpdateFailed_Message,
                Translator.SettingsJunkEmail_ServerUpdateFailed_Title,
                WinoCustomMessageDialogIcon.Warning).ConfigureAwait(false);
        }
    }

    public async Task RemoveSenderAsync(JunkSender sender)
    {
        if (sender == null || SelectedAccount == null)
            return;

        await _junkSenderService.RemoveSenderAsync(SelectedAccount.Id, sender.Address, sender.ListType).ConfigureAwait(false);
        await LoadListsAsync().ConfigureAwait(false);
        await PushToServerAsync(sender.Address, sender.ListType, add: false).ConfigureAwait(false);
    }

    private async Task ImportAsync(JunkListType listType)
    {
        if (SelectedAccount == null)
            return;

        var bytes = await _dialogService.PickWindowsFileContentAsync(".txt", ".csv").ConfigureAwait(false);
        if (bytes == null || bytes.Length == 0)
            return;

        var addresses = Encoding.UTF8.GetString(bytes)
            .Split(ImportSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var added = await _junkSenderService.ImportAsync(SelectedAccount.Id, listType, addresses).ConfigureAwait(false);
        await LoadListsAsync().ConfigureAwait(false);

        await _dialogService.ShowMessageAsync(
            string.Format(Translator.SettingsJunkEmail_ImportResult_Message, added),
            Translator.SettingsJunkEmail_ImportResult_Title,
            WinoCustomMessageDialogIcon.Information).ConfigureAwait(false);
    }

    private async Task ExportAsync(JunkListType listType)
    {
        var addresses = (listType == JunkListType.Safe ? SafeSenders : BlockedSenders)
            .Select(sender => sender.Address)
            .ToList();

        if (addresses.Count == 0)
        {
            await _dialogService.ShowMessageAsync(
                Translator.SettingsJunkEmail_ExportEmpty_Message,
                Translator.SettingsJunkEmail_ExportEmpty_Title,
                WinoCustomMessageDialogIcon.Information).ConfigureAwait(false);
            return;
        }

        var folder = await _dialogService.PickWindowsFolderAsync().ConfigureAwait(false);
        if (string.IsNullOrEmpty(folder))
            return;

        var fileName = listType == JunkListType.Safe ? "safe-senders.txt" : "blocked-senders.txt";
        var content = string.Join(Environment.NewLine, addresses);

        await using (var stream = await _fileService.GetFileStreamAsync(folder, fileName).ConfigureAwait(false))
        {
            var buffer = Encoding.UTF8.GetBytes(content);
            await stream.WriteAsync(buffer).ConfigureAwait(false);
        }
    }

    private async Task LoadListsAsync()
    {
        var account = SelectedAccount;

        IReadOnlyList<JunkSender> safe = account == null
            ? Array.Empty<JunkSender>()
            : await _junkSenderService.GetSendersAsync(account.Id, JunkListType.Safe).ConfigureAwait(false);
        IReadOnlyList<JunkSender> blocked = account == null
            ? Array.Empty<JunkSender>()
            : await _junkSenderService.GetSendersAsync(account.Id, JunkListType.Blocked).ConfigureAwait(false);

        await ExecuteUIThread(() =>
        {
            SafeSenders.Clear();
            foreach (var sender in safe)
                SafeSenders.Add(sender);

            BlockedSenders.Clear();
            foreach (var sender in blocked)
                BlockedSenders.Add(sender);

            OnPropertyChanged(nameof(HasSafeSenders));
            OnPropertyChanged(nameof(HasBlockedSenders));
        }).ConfigureAwait(false);
    }

    // Lenient check: exactly one '@' with non-empty local and domain parts and a dot in the domain
    // (allows sub-addressing and most real addresses without over-validating). Bare domains are not
    // accepted from the page.
    private static bool IsValidEntry(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return false;

        var trimmed = address.Trim();
        var at = trimmed.IndexOf('@');
        return at > 0 && at < trimmed.Length - 1 && trimmed.IndexOf('@', at + 1) < 0 && trimmed[(at + 1)..].Contains('.');
    }
}
