using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Synchronizers.Mail;

namespace Wino.Core.Services;

public class SynchronizerFactory : ISynchronizerFactory
{
    private bool isInitialized = false;

    private readonly IAccountService _accountService;
    private readonly IServiceProvider _serviceProvider;

    private readonly List<IWinoSynchronizerBase> synchronizerCache = new();

    public SynchronizerFactory(IAccountService accountService,
                               IServiceProvider serviceProvider)
    {
        _accountService = accountService;
        _serviceProvider = serviceProvider;
    }

    public async Task<IWinoSynchronizerBase> GetAccountSynchronizerAsync(Guid accountId)
    {
        var synchronizer = synchronizerCache.Find(a => a.Account.Id == accountId);

        if (synchronizer == null)
        {
            var account = await _accountService.GetAccountAsync(accountId);

            if (account != null)
            {
                synchronizer = CreateNewSynchronizer(account);


                return await GetAccountSynchronizerAsync(accountId);
            }
        }

        return synchronizer;
    }

    private IWinoSynchronizerBase CreateIntegratorWithDefaultProcessor(MailAccount mailAccount)
    {
        var providerType = mailAccount.ProviderType;

        // Use ActivatorUtilities to create synchronizers with the account parameter
        // All other dependencies will be resolved from DI container
        switch (providerType)
        {
            case Domain.Enums.MailProviderType.Outlook:
                return ActivatorUtilities.CreateInstance<OutlookSynchronizer>(_serviceProvider, mailAccount);
            case Domain.Enums.MailProviderType.Gmail:
                return ActivatorUtilities.CreateInstance<GmailSynchronizer>(_serviceProvider, mailAccount);
            case Domain.Enums.MailProviderType.IMAP4:
                return ActivatorUtilities.CreateInstance<ImapSynchronizer>(_serviceProvider, mailAccount);
            default:
                break;
        }

        return null;
    }

    public IWinoSynchronizerBase CreateNewSynchronizer(MailAccount account)
    {
        var synchronizer = CreateIntegratorWithDefaultProcessor(account);

        if (synchronizer is IImapSynchronizer imapSynchronizer)
        {
            // Start the idle client for IMAP synchronizer.
            _ = imapSynchronizer.StartIdleClientAsync();

            // Pre-warm the client pool for IMAP synchronizer.
            _ = imapSynchronizer.PreWarmClientPoolAsync();
        }

        synchronizerCache.Add(synchronizer);

        return synchronizer;
    }

    public async Task InitializeAsync()
    {
        if (isInitialized) return;

        var accounts = await _accountService.GetAccountsAsync();

        foreach (var account in accounts)
        {
            CreateNewSynchronizer(account);
        }

        isInitialized = true;
    }

    public async Task DeleteSynchronizerAsync(Guid accountId)
    {
        var synchronizer = synchronizerCache.Find(a => a.Account.Id == accountId);

        if (synchronizer != null)
        {
            // Stop the current synchronization.
            await synchronizer.KillSynchronizerAsync();

            synchronizerCache.Remove(synchronizer);
        }
    }
}
