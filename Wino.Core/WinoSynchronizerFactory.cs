using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Wino.Core.Authenticators;
using Wino.Core.Domain.Entities;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Integration.Processors;
using Wino.Core.Services;
using Wino.Core.Synchronizers;

namespace Wino.Core
{
    /// <summary>
    /// Factory that keeps track of all integrator with associated mail accounts.
    /// Synchronizer per-account makes sense because re-generating synchronizers are not ideal.
    /// Users might interact with multiple accounts in 1 app session.
    /// </summary>
    public class WinoSynchronizerFactory : IWinoSynchronizerFactory
    {
        private readonly List<IBaseSynchronizer> synchronizerCache = new List<IBaseSynchronizer>();

        private bool isInitialized = false;
        private readonly INativeAppService _nativeAppService;
        private readonly ITokenService _tokenService;
        private readonly IFolderService _folderService;
        private readonly IAccountService _accountService;
        private readonly IContactService _contactService;

        private readonly INotificationBuilder _notificationBuilder;
        private readonly ISignatureService _signatureService;
        private readonly IDatabaseService _databaseService;
        private readonly IMimeFileService _mimeFileService;
        private readonly IOutlookChangeProcessor _outlookChangeProcessor;
        private readonly IGmailChangeProcessor _gmailChangeProcessor;
        private readonly IImapChangeProcessor _imapChangeProcessor;

        public WinoSynchronizerFactory(INativeAppService nativeAppService,
                                     ITokenService tokenService,
                                     IFolderService folderService,
                                     IAccountService accountService,
                                     IContactService contactService,
                                     INotificationBuilder notificationBuilder,
                                     ISignatureService signatureService,
                                     IDatabaseService databaseService,
                                     IMimeFileService mimeFileService,
                                     IOutlookChangeProcessor outlookChangeProcessor,
                                     IGmailChangeProcessor gmailChangeProcessor,
                                     IImapChangeProcessor imapChangeProcessor)
        {
            _contactService = contactService;
            _notificationBuilder = notificationBuilder;
            _nativeAppService = nativeAppService;
            _tokenService = tokenService;
            _folderService = folderService;
            _accountService = accountService;
            _signatureService = signatureService;
            _databaseService = databaseService;
            _mimeFileService = mimeFileService;
            _outlookChangeProcessor = outlookChangeProcessor;
            _gmailChangeProcessor = gmailChangeProcessor;
            _imapChangeProcessor = imapChangeProcessor;
        }

        public IBaseSynchronizer GetAccountSynchronizer(Guid accountId)
            => synchronizerCache.Find(a => a.Account.Id == accountId);

        private IBaseSynchronizer CreateIntegratorWithDefaultProcessor(MailAccount mailAccount)
        {
            var providerType = mailAccount.ProviderType;

            switch (providerType)
            {
                case Domain.Enums.MailProviderType.Outlook:
                    var outlookAuthenticator = new OutlookAuthenticator(_tokenService, _nativeAppService);
                    return new OutlookSynchronizer(mailAccount, outlookAuthenticator, _outlookChangeProcessor);
                case Domain.Enums.MailProviderType.Gmail:
                    var gmailAuthenticator = new GmailAuthenticator(_tokenService, _nativeAppService);
                    return new GmailSynchronizer(mailAccount, gmailAuthenticator, _gmailChangeProcessor);
                case Domain.Enums.MailProviderType.Office365:
                    break;
                case Domain.Enums.MailProviderType.Yahoo:
                    break;
                case Domain.Enums.MailProviderType.IMAP4:
                    return new ImapSynchronizer(mailAccount, _imapChangeProcessor);
                default:
                    break;
            }

            return null;
        }

        public void DeleteSynchronizer(MailAccount account)
        {
            var synchronizer = GetAccountSynchronizer(account.Id);

            if (synchronizer == null) return;

            synchronizerCache.Remove(synchronizer);
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

        public IBaseSynchronizer CreateNewSynchronizer(MailAccount account)
        {
            var synchronizer = CreateIntegratorWithDefaultProcessor(account);

            synchronizerCache.Add(synchronizer);

            return synchronizer;
        }
    }
}
