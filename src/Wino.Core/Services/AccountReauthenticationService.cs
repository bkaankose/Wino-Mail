using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Messaging.Server;

namespace Wino.Core.Services;

/// <summary>
/// Re-authenticates an existing Gmail or Outlook account and replays the post-authentication
/// work of account setup, so an account that was restored from a backup or lost its token ends up
/// in the same state as a freshly added one. The account row and its local data are kept.
/// </summary>
public sealed class AccountReauthenticationService(
    IAccountService accountService,
    IAccountProviderFeatureService featureService,
    ISynchronizationManager synchronizationManager,
    IMessenger messenger) : IAccountReauthenticationService
{
    private readonly ILogger _logger = Log.ForContext<AccountReauthenticationService>();

    public async Task<MailAccount> ReauthenticateAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        var account = await accountService.GetAccountAsync(accountId).ConfigureAwait(false)
            ?? throw new InvalidOperationException(Translator.Exception_NullAssignedAccount);

        if (account.ProviderType is not (MailProviderType.Gmail or MailProviderType.Outlook))
            throw new NotSupportedException("Only Gmail and Outlook accounts can be re-authenticated interactively.");

        // The sign-in asks for exactly the modes the user turned on. Provider scopes follow the
        // Granted flags, so they are aligned with Enabled before the request: an enabled provider
        // mode is requested even if it was never granted, a disabled one is never requested.
        // The account is only persisted after a successful sign-in.
        account.IsCalendarAccessGranted = IsProviderModeEnabled(account.IsCalendarAccessEnabled, account.CalendarIntegrationSource);
        account.IsContactAccessGranted = IsProviderModeEnabled(account.IsContactAccessEnabled, account.ContactIntegrationSource);
        account.IsTaskAccessGranted = IsProviderModeEnabled(account.IsTaskAccessEnabled, account.TaskIntegrationSource);

        // Keep the optional scopes the account already holds; a plain sign-in would drop them.
        var features = await featureService.GetFeaturesAsync(accountId, cancellationToken).ConfigureAwait(false);
        var activeFeatures = features
            .Where(feature => feature.AuthorizationState == ProviderFeatureAuthorizationState.Active)
            .Select(feature => feature.Feature)
            .Distinct()
            .ToArray();

        var token = await synchronizationManager.HandleAuthorizationAsync(
            account.ProviderType,
            account,
            account.ProviderType == MailProviderType.Gmail,
            forceInteractive: true,
            requestedFeatures: activeFeatures).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        if (token == null)
            throw new InvalidOperationException("Authentication completed without a token.");

        var tokenAddress = token.AccountAddress?.Trim();
        if (!string.IsNullOrWhiteSpace(tokenAddress) &&
            !string.Equals(account.Address?.Trim(), tokenAddress, StringComparison.OrdinalIgnoreCase))
        {
            // Signing in to a mailbox that another account already owns would merge two mailboxes.
            if (await accountService.AccountAddressExistsAsync(tokenAddress, account.Id).ConfigureAwait(false))
                throw new InvalidOperationException(Translator.DialogMessage_AccountAddressExistsMessage);

            account.Address = tokenAddress;
        }

        if (!string.IsNullOrWhiteSpace(token.AuthenticationAddress))
            account.AuthenticationAddress = token.AuthenticationAddress;

        // This sign-in covered every enabled mode, so none of them waits for consent any more.
        account.IsContactReauthorizationRequired = false;
        account.IsTaskReauthorizationRequired = false;

        // A re-consent-only fix has no attention to clear; other attention kinds need other fixes.
        if (account.AttentionReason == AccountAttentionReason.InvalidCredentials)
            account.AttentionReason = AccountAttentionReason.None;

        // Publishes AccountUpdatedMessage, which removes the fix entry from the shell menus.
        await accountService.UpdateAccountAsync(account).ConfigureAwait(false);

        // A synchronizer created while the account was blocked still holds the old sign-in identity.
        await synchronizationManager.DestroySynchronizerAsync(account.Id).ConfigureAwait(false);

        return account;
    }

    public async Task SynchronizeAfterReauthenticationAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        var account = await accountService.GetAccountAsync(accountId).ConfigureAwait(false);
        if (account == null)
            return;

        if (account.AttentionReason != AccountAttentionReason.None)
        {
            _logger.Information("Skipping post-authentication synchronization for {AccountId}: the account still needs attention.", accountId);
            return;
        }

        // Calendar, contacts and To Do do not depend on mail folders, profile or aliases. They are
        // queued before the mail setup steps so a restored account shows its non-mail data while
        // mail is still downloading. Each mode runs under its own per-account gate.
        QueueNonMailInitialSynchronizations(account);

        if (!account.IsMailAccessGranted)
            return;

        await SynchronizeMailSetupDataAsync(account, cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        // Re-read: profile sync can change the address and the synchronizers may have updated flags.
        account = await accountService.GetAccountAsync(accountId).ConfigureAwait(false) ?? account;

        QueueMailInitialSynchronization(account);
    }

    private async Task SynchronizeMailSetupDataAsync(MailAccount account, CancellationToken cancellationToken)
    {
        if (account.IsProfileInfoSyncSupported)
        {
            await RunStepAsync(account.Id, "ProfileSync", async () =>
            {
                var result = await synchronizationManager.SynchronizeProfileAsync(account.Id, cancellationToken).ConfigureAwait(false);
                if (result?.ProfileInformation != null)
                {
                    await accountService.UpdateProfileInformationAsync(account.Id, result.ProfileInformation).ConfigureAwait(false);
                }
                else
                {
                    _logger.Warning(result?.Exception, "Profile synchronization for {AccountId} returned no profile ({State}).",
                        account.Id, result?.CompletedState);
                }
            }).ConfigureAwait(false);
        }

        // Restored accounts have no folders yet. Folder metadata comes first so the full
        // synchronization and the parked backup folder layout have something to attach to.
        await RunStepAsync(account.Id, "FolderSync", () =>
            synchronizationManager.SynchronizeFoldersAsync(account.Id, cancellationToken)).ConfigureAwait(false);

        if (account.IsCategorySyncSupported)
        {
            await RunStepAsync(account.Id, "CategorySync", () =>
                synchronizationManager.SynchronizeCategoriesAsync(account.Id, cancellationToken)).ConfigureAwait(false);
        }

        await RunStepAsync(account.Id, "AliasSync", async () =>
        {
            var address = (await accountService.GetAccountAsync(account.Id).ConfigureAwait(false))?.Address ?? account.Address;
            var aliases = await accountService.GetAccountAliasesAsync(account.Id).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(address) && aliases?.Any(alias => alias.IsRootAlias) != true)
                await accountService.CreateRootAliasAsync(account.Id, address).ConfigureAwait(false);

            if (account.IsAliasSyncSupported)
                await synchronizationManager.SynchronizeAliasesAsync(account.Id, cancellationToken).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Mirrors the initial mail synchronization that runs after account setup, through the same
    /// message the app host already handles.
    /// </summary>
    private void QueueMailInitialSynchronization(MailAccount account)
    {
        if (!account.IsMailAccessGranted)
            return;

        messenger.Send(new NewMailSynchronizationRequested(new MailSynchronizationOptions
        {
            AccountId = account.Id,
            Type = MailSynchronizationType.FullFolders
        }));
    }

    /// <summary>
    /// Mirrors the initial calendar, contact and task synchronization that runs after account
    /// setup. The app host runs each request on the thread pool without awaiting the others.
    /// </summary>
    private void QueueNonMailInitialSynchronizations(MailAccount account)
    {
        if (account.IsCalendarAccessEnabled && account.IsCalendarAccessGranted)
        {
            // CalendarEvents refreshes the calendar list before it downloads events.
            messenger.Send(new NewCalendarSynchronizationRequested(new CalendarSynchronizationOptions
            {
                AccountId = account.Id,
                Type = CalendarSynchronizationType.CalendarEvents
            }));
        }

        if (account.IsContactAccessEnabled && account.IsContactAccessGranted && !account.IsContactReauthorizationRequired)
        {
            messenger.Send(new NewContactSynchronizationRequested(new ContactSynchronizationOptions
            {
                AccountId = account.Id,
                Type = ContactSynchronizationType.Delta
            }));
        }

        if (account.IsTaskAccessEnabled && account.IsTaskAccessGranted && !account.IsTaskReauthorizationRequired)
        {
            messenger.Send(new NewTaskSynchronizationRequested(new TaskSynchronizationOptions
            {
                AccountId = account.Id,
                Type = TaskSynchronizationType.Delta
            }));
        }
    }

    private static bool IsProviderModeEnabled(bool isEnabled, AccountIntegrationSource source)
        => isEnabled && source == AccountIntegrationSource.Provider;

    private async Task RunStepAsync(Guid accountId, string stepName, Func<Task> step)
    {
        try
        {
            await step().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Post-authentication step {Step} failed for account {AccountId}.", stepName, accountId);
        }
    }
}
