#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Messaging.Client.Navigation;
using Wino.Core.Domain.Messaging.UI;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.ViewModels;

/// <summary>
/// Accounts picked from other mail clients. Each one runs this page from its address step with the address, the name
/// and the sender name the other client had; nothing is saved until the user adds it like any other account.
/// </summary>
public partial class AccountSetupPageViewModel
{
    private IReadOnlyList<MailClientImportCandidate>? _importedAccounts;
    private int _importIndex;
    private MailAccount? _lastImportedAccount;

    private MailClientImportCandidate? ImportedAccount
        => _importedAccounts != null && _importIndex >= 0 && _importIndex < _importedAccounts.Count ? _importedAccounts[_importIndex] : null;

    public bool IsImporting => ImportedAccount != null;

    private bool HasNextImportedAccount => _importedAccounts != null && _importIndex + 1 < _importedAccounts.Count;

    public bool IsSkipImportedAccountVisible => IsImporting && Step == AccountSetupStep.Address;

    public string SkipImportedAccountText => HasNextImportedAccount
        ? Translator.MailClientImport_SkipAccount
        : Translator.MailClientImport_SkipLastAccount;

    private string ImportedAccountSubtitle => ImportedAccount is { } account && _importedAccounts != null
        ? string.Format(Translator.MailClientImport_SetupSubtitle, _importIndex + 1, _importedAccounts.Count, MailClientImportItem.GetSourceName(account.Source))
        : string.Empty;

    /// <summary>Reads the import queue from the navigation and fills the address step with the current account.</summary>
    private void LoadImportedAccount(ProviderSelectionNavigationContext? context, bool isBackNavigation)
    {
        _importedAccounts = context?.ImportedAccounts is { Count: > 0 } accounts ? accounts : null;
        _importIndex = context?.ImportIndex ?? 0;
        _lastImportedAccount = context?.LastImportedAccount;

        if (isBackNavigation || ImportedAccount is not { } account)
            return;

        EmailAddress = account.Address;
        AccountName = string.Equals(account.AccountName, account.Address, StringComparison.OrdinalIgnoreCase) ? string.Empty : account.AccountName;
        SenderName = account.SenderName;
    }

    /// <summary>
    /// The provider the other client used, so setup does not guess again. A plain IMAP account is still discovered.
    /// Null once the user typed another address.
    /// </summary>
    private AccountSetupProviderChoice? GetImportedProviderChoice(string address)
    {
        if (ImportedAccount is not { } account || !string.Equals(account.Address, address, StringComparison.OrdinalIgnoreCase))
            return null;

        AccountSetupProviderChoiceKind? kind = account.ProviderType switch
        {
            MailProviderType.Gmail => AccountSetupProviderChoiceKind.Google,
            MailProviderType.Outlook => AccountSetupProviderChoiceKind.Microsoft,
            MailProviderType.Exchange => AccountSetupProviderChoiceKind.Exchange,
            MailProviderType.POP3 => AccountSetupProviderChoiceKind.Pop3,
            MailProviderType.IMAP4 when account.SpecialImapProvider == SpecialImapProvider.iCloud => AccountSetupProviderChoiceKind.ICloud,
            MailProviderType.IMAP4 when account.SpecialImapProvider == SpecialImapProvider.Yahoo => AccountSetupProviderChoiceKind.Yahoo,
            _ => null
        };

        return kind is { } value ? ProviderChoices.FirstOrDefault(choice => choice.Kind == value) : null;
    }

    /// <summary>The servers the other client used, for Advanced setup when discovery finds none.</summary>
    private CustomServerInformation? GetImportedServerInformation(string address)
        => ImportedAccount is { ProviderType: MailProviderType.IMAP4 or MailProviderType.POP3, ServerInformation: { } server } account &&
           string.Equals(account.Address, address, StringComparison.OrdinalIgnoreCase)
            ? server
            : null;

    private bool IsImportedPop3Account => ImportedAccount?.ProviderType == MailProviderType.POP3;

    [RelayCommand]
    private async Task SkipImportedAccountAsync()
    {
        CancelOperation();
        await DiscardSignInAsync();
        ContinueImport();
    }

    /// <summary>
    /// An imported account does not take the shell to its mode yet: the next imported account's setup would be lost.
    /// </summary>
    private AccountCreatedMessage CreateAccountCreatedMessage(MailAccount account)
    {
        if (IsImporting)
            _lastImportedAccount = account;

        return new AccountCreatedMessage(account, ShowsAccount: !IsImporting);
    }

    /// <summary>
    /// An imported account skips the result step: an InfoBar says it was added and the next account's setup opens.
    /// </summary>
    private void ContinueImportAfterAdding(MailAccount account)
    {
        if (!IsImporting)
            return;

        var name = string.IsNullOrWhiteSpace(account.Name) ? account.Address : account.Name;
        _dialogService.InfoBarMessage(Translator.GeneralTitle_Info,
                                      string.Format(Translator.MailClientImport_AccountAdded, name),
                                      InfoBarMessageType.Success);
        ContinueImport();
    }

    /// <summary>
    /// Leaves this account's setup and opens the next imported account. After the last one the shell shows the last
    /// account the import added, as it shows a single new account.
    /// </summary>
    private void ContinueImport()
    {
        Messenger.Send(new BackBreadcrumNavigationRequested(NavigationTransitionEffect.FromLeft));

        if (HasNextImportedAccount && _importedAccounts != null)
        {
            Messenger.Send(new BreadcrumbNavigationRequested(
                Translator.MailClientImport_Title,
                WinoPage.AccountSetupPage,
                ProviderSelectionNavigationContext.CreateForImport(_importedAccounts, _importIndex + 1, _lastImportedAccount)));
            return;
        }

        if (_lastImportedAccount != null)
            Messenger.Send(new CreatedAccountShowRequested(_lastImportedAccount));
    }
}
