using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Integration.Processors;
using Wino.Core.Synchronizers.ImapSync;
using Wino.Core.Synchronizers.Mail;

namespace Wino.Core.Services;

public class SynchronizerFactory : ISynchronizerFactory
{
    private bool isInitialized = false;

    private readonly IAccountService _accountService;
    private readonly IApplicationConfiguration _applicationConfiguration;
    private readonly IOutlookSynchronizerErrorHandlerFactory _outlookSynchronizerErrorHandlerFactory;
    private readonly IGmailSynchronizerErrorHandlerFactory _gmailSynchronizerErrorHandlerFactory;
    private readonly IImapSynchronizerErrorHandlerFactory _imapSynchronizerErrorHandlerFactory;
    private readonly IOutlookChangeProcessor _outlookChangeProcessor;
    private readonly IGmailChangeProcessor _gmailChangeProcessor;
    private readonly IImapChangeProcessor _imapChangeProcessor;
    private readonly IAuthenticationProvider _authenticationProvider;
    private readonly UnifiedImapSynchronizer _unifiedImapSynchronizer;
    private readonly ICalDavClient _calDavClient;
    private readonly IAutoDiscoveryService _autoDiscoveryService;
    private readonly ICalendarService _calendarService;
    private readonly IMailCategoryService _mailCategoryService;

    private readonly List<IWinoSynchronizerBase> synchronizerCache = new();

    public SynchronizerFactory(IOutlookChangeProcessor outlookChangeProcessor,
                               IGmailChangeProcessor gmailChangeProcessor,
                               IImapChangeProcessor imapChangeProcessor,
                               IAuthenticationProvider authenticationProvider,
                               IAccountService accountService,
                               IApplicationConfiguration applicationConfiguration,
                               IOutlookSynchronizerErrorHandlerFactory outlookSynchronizerErrorHandlerFactory,
                               IGmailSynchronizerErrorHandlerFactory gmailSynchronizerErrorHandlerFactory,
                               IImapSynchronizerErrorHandlerFactory imapSynchronizerErrorHandlerFactory,
                               UnifiedImapSynchronizer unifiedImapSynchronizer,
                               ICalDavClient calDavClient,
                               IAutoDiscoveryService autoDiscoveryService,
                               ICalendarService calendarService,
                               IMailCategoryService mailCategoryService)
    {
        _outlookChangeProcessor = outlookChangeProcessor;
        _gmailChangeProcessor = gmailChangeProcessor;
        _imapChangeProcessor = imapChangeProcessor;
        _authenticationProvider = authenticationProvider;
        _accountService = accountService;
        _applicationConfiguration = applicationConfiguration;
        _outlookSynchronizerErrorHandlerFactory = outlookSynchronizerErrorHandlerFactory;
        _gmailSynchronizerErrorHandlerFactory = gmailSynchronizerErrorHandlerFactory;
        _imapSynchronizerErrorHandlerFactory = imapSynchronizerErrorHandlerFactory;
        _unifiedImapSynchronizer = unifiedImapSynchronizer;
        _calDavClient = calDavClient;
        _autoDiscoveryService = autoDiscoveryService;
        _calendarService = calendarService;
        _mailCategoryService = mailCategoryService;
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

        switch (providerType)
        {
            case Domain.Enums.MailProviderType.Outlook:
                var outlookAuthenticator = _authenticationProvider.GetAuthenticator(Domain.Enums.MailProviderType.Outlook) as IOutlookAuthenticator;
                return new OutlookSynchronizer(mailAccount, outlookAuthenticator, _outlookChangeProcessor, _outlookSynchronizerErrorHandlerFactory, _mailCategoryService);
            case Domain.Enums.MailProviderType.Gmail:
                var gmailAuthenticator = _authenticationProvider.GetAuthenticator(Domain.Enums.MailProviderType.Gmail) as IGmailAuthenticator;
                return new GmailSynchronizer(mailAccount, gmailAuthenticator, _gmailChangeProcessor, _gmailSynchronizerErrorHandlerFactory);
            case Domain.Enums.MailProviderType.IMAP4:
                return new ImapSynchronizer(mailAccount, _imapChangeProcessor, _applicationConfiguration, _unifiedImapSynchronizer, _imapSynchronizerErrorHandlerFactory, _calDavClient, _autoDiscoveryService, _calendarService);
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
