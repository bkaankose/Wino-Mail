using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Synchronizers;

namespace Wino.Core.Services;

public sealed partial class AccountCapabilityService
{
    public async Task<MailAccount> SetAppModeAsync(
        MailAccount account,
        WinoApplicationMode app,
        AccountCapabilityMode mode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);

        var traits = _providers.GetTraits(account);
        var state = AccountApps.Evaluate(account, traits, app);

        if (state.Mode == mode)
            return account;

        if (mode == AccountCapabilityMode.Provider && !state.IsServiceOffered ||
            mode == AccountCapabilityMode.Local && !state.IsDeviceOffered)
        {
            throw new NotSupportedException($"{account.ProviderType} accounts do not offer {mode} for {app}.");
        }

        if (mode == AccountCapabilityMode.Off && AccountApps.IsLastAppOn(account, traits, app))
            throw new InvalidOperationException(Translator.AccountApps_LastAppMessage);

        if (mode == AccountCapabilityMode.Provider && state.IsServiceDavServer && !state.HasDavServer)
            throw new InvalidOperationException(Translator.AccountApps_ServerMissingMessage);

        var previous = CapabilityFlags.Capture(account);
        var previousCalendarSupportMode = account.ServerInformation?.CalendarSupportMode;
        var serverTaskSource = ServerTaskSource(traits);
        var existingServerTaskListIds = app == WinoApplicationMode.Tasks && mode == AccountCapabilityMode.Provider
            ? (await _taskService.GetTaskListsAsync(account.Id).ConfigureAwait(false))
                .Where(list => list.SourceKind == serverTaskSource)
                .Select(list => list.Id)
                .ToHashSet()
            : [];

        ApplyAppMode(account, traits, app, mode);

        var isCommitted = false;

        try
        {
            if (mode == AccountCapabilityMode.Provider)
                await ConnectServerSourceAsync(account, traits, app, cancellationToken).ConfigureAwait(false);

            if (app == WinoApplicationMode.Contacts)
                account.IsContactReauthorizationRequired = false;
            else if (app == WinoApplicationMode.Tasks)
                account.IsTaskReauthorizationRequired = false;

            if (account.ServerInformation is { } server && server.CalendarSupportMode != previousCalendarSupportMode)
            {
                var storedServer = server.CloneForCredentialStorage();
                await _accountService.UpdateImapConnectionSettingsAsync(account, storedServer).ConfigureAwait(false);
            }
            else
            {
                await _accountService.UpdateAccountAsync(account).ConfigureAwait(false);
            }

            isCommitted = true;
        }
        catch
        {
            previous.Restore(account);
            RestoreCalendarSupportMode(account, previousCalendarSupportMode);

            if (mode == AccountCapabilityMode.Provider)
                await RemoveUncommittedServerDataAsync(account, traits, app, state.Mode, existingServerTaskListIds).ConfigureAwait(false);

            throw;
        }

        if (isCommitted)
            await CompleteAppModeChangeAsync(account, traits, app, state.Mode, mode).ConfigureAwait(false);

        return await _accountService.GetAccountAsync(account.Id).ConfigureAwait(false);
    }

    /// <summary>Writes the columns that make up one app's state. The previous source is kept when the app turns off.</summary>
    private static void ApplyAppMode(MailAccount account, MailProviderTraits traits, WinoApplicationMode app, AccountCapabilityMode mode)
    {
        var serverSource = traits.ServesModesFromProvider ? AccountIntegrationSource.Provider : AccountIntegrationSource.Dav;
        var isOn = mode != AccountCapabilityMode.Off;
        var isServer = mode == AccountCapabilityMode.Provider;

        switch (app)
        {
            case WinoApplicationMode.Mail:
                account.IsMailAccessGranted = isOn;
                break;
            case WinoApplicationMode.Calendar:
                account.IsCalendarAccessEnabled = isOn;

                // IMAP and POP3 mark a calendar on this device as granted too; only the support mode tells them apart.
                account.IsCalendarAccessGranted = traits.ServesModesFromProvider ? isServer : isOn;

                if (isOn)
                    account.CalendarIntegrationSource = isServer ? serverSource : AccountIntegrationSource.Local;

                if (account.ServerInformation != null && !traits.ServesModesFromProvider)
                {
                    account.ServerInformation.CalendarSupportMode = mode switch
                    {
                        AccountCapabilityMode.Provider => ImapCalendarSupportMode.CalDav,
                        AccountCapabilityMode.Local => ImapCalendarSupportMode.LocalOnly,
                        _ => ImapCalendarSupportMode.Disabled
                    };
                }

                break;
            case WinoApplicationMode.Contacts:
                account.IsContactAccessEnabled = isOn;
                account.IsContactAccessGranted = isServer;

                if (isOn)
                    account.ContactIntegrationSource = isServer ? serverSource : AccountIntegrationSource.Local;

                break;
            case WinoApplicationMode.Tasks:
                account.IsTaskAccessEnabled = isOn;
                account.IsTaskAccessGranted = isServer;

                if (isOn)
                    account.TaskIntegrationSource = isServer ? serverSource : AccountIntegrationSource.Local;

                break;
        }
    }

    private static void RestoreCalendarSupportMode(MailAccount account, ImapCalendarSupportMode? mode)
    {
        if (account.ServerInformation != null && mode.HasValue)
            account.ServerInformation.CalendarSupportMode = mode.Value;
    }

    /// <summary>
    /// Signs in where the provider asks for consent, then synchronizes the app once. A failure here leaves the account
    /// as it was, because nothing has been saved yet.
    /// </summary>
    private async Task ConnectServerSourceAsync(MailAccount account, MailProviderTraits traits, WinoApplicationMode app, CancellationToken cancellationToken)
    {
        if (traits.UsesInteractiveOAuth)
        {
            await _synchronizationManager.HandleAuthorizationAsync(account.ProviderType, account, forceInteractive: true).ConfigureAwait(false);

            if (app == WinoApplicationMode.Tasks &&
                _authenticationProvider?.GetAuthenticator(account.ProviderType) is ISubstrateTaskTokenProvider substrateTokenProvider)
            {
                try
                {
                    await substrateTokenProvider.EnsureSubstrateTaskConsentAsync(account).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Exchange consent is optional. Graph task sync remains available.
                }
            }
        }

        // The cached synchronizer is rebuilt from the stored account whenever its access differs, so it would run with
        // the old state. A synchronizer of its own, built from the account as it would be saved, talks to the server.
        var module = _providers.Find(account.ProviderType)
                     ?? throw new NotSupportedException($"No provider is registered for {account.ProviderType}.");
        var probe = module.CreateSynchronizer(account)
                    ?? throw new NotSupportedException($"{account.ProviderType} cannot synchronize.");

        if (probe is IProviderTraitsReceiver receiver)
            receiver.ProviderTraits = module.GetTraits(account);

        try
        {
            switch (app)
            {
                case WinoApplicationMode.Mail:
                    {
                        var result = await probe.SynchronizeMailsAsync(new MailSynchronizationOptions
                        {
                            AccountId = account.Id,
                            Type = MailSynchronizationType.FoldersOnly
                        }, cancellationToken).ConfigureAwait(false);
                        ThrowIfFailed(result.CompletedState, result.Exception, Translator.AccountApps_ConnectFailedMessage);
                        break;
                    }
                case WinoApplicationMode.Calendar:
                    {
                        var result = await probe.SynchronizeCalendarEventsAsync(new CalendarSynchronizationOptions
                        {
                            AccountId = account.Id,
                            Type = CalendarSynchronizationType.CalendarMetadata
                        }, cancellationToken).ConfigureAwait(false);
                        ThrowIfFailed(result.CompletedState, result.Exception, Translator.AccountApps_ConnectFailedMessage);
                        break;
                    }
                case WinoApplicationMode.Contacts:
                    {
                        var result = await probe.SynchronizeContactsAsync(new ContactSynchronizationOptions
                        {
                            AccountId = account.Id,
                            Type = ContactSynchronizationType.Full
                        }, cancellationToken).ConfigureAwait(false);
                        ThrowIfFailed(result.CompletedState, result.Exception, Translator.AccountApps_ConnectFailedMessage);
                        break;
                    }
                case WinoApplicationMode.Tasks:
                    {
                        var result = await probe.SynchronizeTasksAsync(new TaskSynchronizationOptions
                        {
                            AccountId = account.Id,
                            Type = TaskSynchronizationType.Full,
                            IsUserInitiated = true
                        }, cancellationToken).ConfigureAwait(false);
                        ThrowIfFailed(result.CompletedState, result.Exception, Translator.AccountApps_ConnectFailedMessage);
                        break;
                    }
            }
        }
        finally
        {
            try
            {
                await probe.KillSynchronizerAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Stopping the synchronizer that connected {App} of account {AccountId} failed", app, account.Id);
            }
        }
    }

    private static void ThrowIfFailed(SynchronizationCompletedState state, Exception exception, string fallbackMessage)
    {
        if (state == SynchronizationCompletedState.Success)
            return;

        throw exception ?? new InvalidOperationException(fallbackMessage);
    }

    /// <summary>Removes what the first synchronization of a server source stored before the change was refused.</summary>
    private async Task RemoveUncommittedServerDataAsync(
        MailAccount account,
        MailProviderTraits traits,
        WinoApplicationMode app,
        AccountCapabilityMode previousMode,
        HashSet<Guid> existingServerTaskListIds)
    {
        try
        {
            switch (app)
            {
                case WinoApplicationMode.Mail:
                    await _synchronizationManager.CancelSynchronizationsAsync(account.Id).ConfigureAwait(false);
                    await _accountService.DeleteAccountMailDataAsync(account.Id).ConfigureAwait(false);
                    break;
                case WinoApplicationMode.Calendar:
                    await _synchronizationManager.CancelSynchronizationsAsync(account.Id).ConfigureAwait(false);
                    await _calendarService.DeleteAccountCalendarDataAsync(account.Id).ConfigureAwait(false);

                    if (previousMode == AccountCapabilityMode.Local)
                        await _accountService.EnsureDefaultLocalCalendarAsync(account.Id).ConfigureAwait(false);

                    break;
                case WinoApplicationMode.Contacts:
                    await _contactService.DeleteAddressBooksBySourceAsync(account.Id, ServerContactSource(traits)).ConfigureAwait(false);
                    break;
                case WinoApplicationMode.Tasks:
                    var source = ServerTaskSource(traits);
                    var lists = await _taskService.GetTaskListsAsync(account.Id).ConfigureAwait(false);
                    foreach (var list in lists.Where(list => list.SourceKind == source && !existingServerTaskListIds.Contains(list.Id)))
                        await _taskService.RemoveTaskListAsync(list.Id).ConfigureAwait(false);

                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Removing the uncommitted {App} data of account {AccountId} failed", app, account.Id);
        }
    }

    /// <summary>
    /// Runs after the account is saved: removes the data the change leaves behind and prepares the store on this
    /// device. The caller requests the full synchronization. Failures here are logged; the change itself stands.
    /// </summary>
    private async Task CompleteAppModeChangeAsync(
        MailAccount account,
        MailProviderTraits traits,
        WinoApplicationMode app,
        AccountCapabilityMode from,
        AccountCapabilityMode to)
    {
        try
        {
            var isLeavingServer = from == AccountCapabilityMode.Provider && to != AccountCapabilityMode.Provider;

            if (to == AccountCapabilityMode.Off || isLeavingServer)
                await _synchronizationManager.CancelSynchronizationsAsync(account.Id).ConfigureAwait(false);

            switch (app)
            {
                case WinoApplicationMode.Mail:
                    if (to == AccountCapabilityMode.Off)
                        await _accountService.DeleteAccountMailDataAsync(account.Id).ConfigureAwait(false);
                    else
                        await CompleteMailTurnOnAsync(account, traits).ConfigureAwait(false);

                    break;
                case WinoApplicationMode.Calendar:
                    // Calendars carry no source of their own, so a server's first synchronization replaces the ones on
                    // this device and leaving a server starts over with one empty calendar.
                    if (to == AccountCapabilityMode.Off || isLeavingServer)
                        await _calendarService.DeleteAccountCalendarDataAsync(account.Id).ConfigureAwait(false);

                    if (to == AccountCapabilityMode.Local)
                        await _accountService.EnsureDefaultLocalCalendarAsync(account.Id).ConfigureAwait(false);

                    break;
                case WinoApplicationMode.Contacts:
                    if (to == AccountCapabilityMode.Off)
                        await _contactService.DeleteAccountContactsAsync(account.Id).ConfigureAwait(false);
                    else if (isLeavingServer)
                        await _contactService.DeleteAddressBooksBySourceAsync(account.Id, ServerContactSource(traits)).ConfigureAwait(false);

                    if (to == AccountCapabilityMode.Local)
                        await _contactService.EnsureLocalAddressBookAsync(account.Id, account.Name).ConfigureAwait(false);

                    break;
                case WinoApplicationMode.Tasks:
                    if (to == AccountCapabilityMode.Off)
                        await _taskService.DeleteAccountTasksAsync(account.Id).ConfigureAwait(false);
                    else if (isLeavingServer)
                        await _taskService.DeleteTaskListsBySourceAsync(account.Id, ServerTaskSource(traits)).ConfigureAwait(false);

                    if (to == AccountCapabilityMode.Local)
                        await _taskService.EnsureLocalTaskListAsync(account.Id, account.Name).ConfigureAwait(false);

                    break;
            }

            // The synchronizer reads the app's source when it is created, and its cached account still has the old state.
            await _synchronizationManager.DestroySynchronizerAsync(account.Id).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Completing the {App} change of account {AccountId} from {From} to {To} failed", app, account.Id, from, to);
        }
    }

    private async Task CompleteMailTurnOnAsync(MailAccount account, MailProviderTraits traits)
    {
        if (!string.IsNullOrWhiteSpace(account.Address))
        {
            var aliases = await _accountService.GetAccountAliasesAsync(account.Id).ConfigureAwait(false);
            if (!aliases.Any(alias => alias.IsRootAlias))
                await _accountService.CreateRootAliasAsync(account.Id, account.Address).ConfigureAwait(false);
        }

        if (traits.SupportsProfileInfoSync)
        {
            var profile = await _synchronizationManager.SynchronizeProfileAsync(account.Id).ConfigureAwait(false);
            if (profile.ProfileInformation != null)
                await _accountService.UpdateProfileInformationAsync(account.Id, profile.ProfileInformation).ConfigureAwait(false);
        }

        if (traits.SupportsAliasSync)
            await _synchronizationManager.SynchronizeAliasesAsync(account.Id).ConfigureAwait(false);

        if (traits.SupportsCategorySync)
            await _synchronizationManager.SynchronizeCategoriesAsync(account.Id).ConfigureAwait(false);
    }

    private static ContactSourceKind ServerContactSource(MailProviderTraits traits)
        => traits.ServesModesFromProvider ? ProviderContactSource(traits) : ContactSourceKind.CardDav;

    private static TaskSourceKind ServerTaskSource(MailProviderTraits traits)
        => traits.ServesModesFromProvider ? ProviderTaskSource(traits) : TaskSourceKind.CalDav;
}
