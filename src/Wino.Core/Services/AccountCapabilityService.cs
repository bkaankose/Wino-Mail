using System;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Synchronization;

namespace Wino.Core.Services;

public sealed class AccountCapabilityService : IAccountCapabilityService
{
    private readonly ISynchronizationManager _synchronizationManager;
    private readonly IAccountService _accountService;
    private readonly IContactService _contactService;

    public AccountCapabilityService(
        ISynchronizationManager synchronizationManager,
        IAccountService accountService,
        IContactService contactService)
    {
        _synchronizationManager = synchronizationManager;
        _accountService = accountService;
        _contactService = contactService;
    }

    public async Task<MailAccount> ApplyAsync(
        MailAccount account,
        bool includeMail,
        bool includeCalendar,
        bool includeContacts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (!includeMail && !includeCalendar && !includeContacts)
            throw new InvalidOperationException("At least one account capability must remain enabled.");
        if (includeContacts && account.ProviderType is not (MailProviderType.Gmail or MailProviderType.Outlook))
            throw new NotSupportedException("Provider contacts are available only for Gmail and Outlook accounts.");

        var previousMail = account.IsMailAccessGranted;
        var previousCalendar = account.IsCalendarAccessGranted;
        var previousContacts = account.IsContactAccessGranted;
        var synchronizer = await _synchronizationManager.GetSynchronizerAsync(account.Id).ConfigureAwait(false);
        var synchronizerAccount = synchronizer?.Account;

        account.IsMailAccessGranted = includeMail;
        account.IsCalendarAccessGranted = includeCalendar;
        account.IsContactAccessGranted = includeContacts;
        if (synchronizerAccount is not null)
        {
            synchronizerAccount.IsMailAccessGranted = includeMail;
            synchronizerAccount.IsCalendarAccessGranted = includeCalendar;
            synchronizerAccount.IsContactAccessGranted = includeContacts;
        }

        try
        {
            await _synchronizationManager.HandleAuthorizationAsync(
                account.ProviderType,
                account,
                account.ProviderType == MailProviderType.Gmail,
                forceInteractive: true).ConfigureAwait(false);

            if (includeContacts && !previousContacts)
            {
                var result = await _synchronizationManager.SynchronizeContactsAsync(new ContactSynchronizationOptions
                {
                    AccountId = account.Id,
                    Type = ContactSynchronizationType.Full
                }, cancellationToken).ConfigureAwait(false);
                if (result.CompletedState != SynchronizationCompletedState.Success)
                    throw result.Exception ?? new InvalidOperationException("Contact synchronization failed.");

                await _contactService.DeleteAddressBooksBySourceAsync(account.Id, ContactSourceKind.Local).ConfigureAwait(false);
            }
            else if (!includeContacts && previousContacts)
            {
                var source = account.ProviderType == MailProviderType.Gmail ? ContactSourceKind.Gmail : ContactSourceKind.Outlook;
                await _contactService.DeleteAddressBooksBySourceAsync(account.Id, source).ConfigureAwait(false);
                await _contactService.EnsureLocalAddressBookAsync(account.Id, account.Name).ConfigureAwait(false);
            }

            account.IsContactReauthorizationRequired = false;
            await _accountService.UpdateAccountAsync(account).ConfigureAwait(false);
            return await _accountService.GetAccountAsync(account.Id).ConfigureAwait(false);
        }
        catch
        {
            account.IsMailAccessGranted = previousMail;
            account.IsCalendarAccessGranted = previousCalendar;
            account.IsContactAccessGranted = previousContacts;
            if (synchronizerAccount is not null)
            {
                synchronizerAccount.IsMailAccessGranted = previousMail;
                synchronizerAccount.IsCalendarAccessGranted = previousCalendar;
                synchronizerAccount.IsContactAccessGranted = previousContacts;
            }

            if (!previousContacts)
            {
                var source = account.ProviderType == MailProviderType.Gmail ? ContactSourceKind.Gmail : ContactSourceKind.Outlook;
                await _contactService.DeleteAddressBooksBySourceAsync(account.Id, source).ConfigureAwait(false);
            }
            throw;
        }
    }
}
