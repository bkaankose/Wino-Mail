using Wino.Core.Authenticators;
using Wino.Core.Domain.Entities;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Integration.Processors;
using Wino.Core.Integration.Threading;
using Wino.Core.Services;
using Wino.Core.Synchronizers;
using Wino.Mail.ConsoleTest.Services;

namespace Wino.Mail.ConsoleTest
{
    public class OutlookTest : BaseSynchronizationTest, ISynchronizerTest
    {
        private Guid AccountId { get; } = Guid.NewGuid();
        public MailAccount Account { get; private set; }
        public IBaseSynchronizer Synchronizer { get; private set; }

        public async Task InitializeTestAsync()
        {
            // Remove the existing database.



            var init = new ConsoleAppInitializerService();
            var dbService = new DatabaseService(init);

            await dbService.InitializeAsync();

            await SetupAccountAsync(dbService);

            var tokenService = new TokenService(dbService);
            var nt = new ConsoleNativeAppService();
            var signatureService = new SignatureService(dbService);
            var accProvider = new AuthenticationProvider(nt, tokenService);
            var accService = new AccountService(dbService, accProvider, signatureService, null);
            var mimeService = new MimeFileService(nt);
            var folderService = new FolderService(dbService, accService, mimeService);
            var contactService = new ContactService(dbService);

            var outlookThreadingProvider = new OutlookThreadingStrategy(dbService, folderService);
            var threadProvider = new ThreadingStrategyProvider(outlookThreadingProvider, null, null);
            var mailService = new MailService(dbService, folderService, contactService, accService, signatureService, threadProvider, mimeService);

            var outlookAuthenticator = new OutlookAuthenticator(tokenService, nt);
            var processor = new OutlookChangeProcessor(dbService, folderService, mailService, accService, mimeService);
            Synchronizer = new OutlookSynchronizer(Account, outlookAuthenticator, processor);
        }

        private async Task SetupAccountAsync(IDatabaseService databaseService)
        {
            var preferences = new MailAccountPreferences()
            {
                AccountId = AccountId,
                Id = Guid.NewGuid(),
                IsFocusedInboxEnabled = true,
                IsNotificationsEnabled = true
            };

            Account = new MailAccount()
            {
                Id = AccountId,
                Address = "bkaankose@outlook.com",
                Name = "Personal Outlook",
                Preferences = preferences,
                ProfileName = "Burak Kaan Kose",
                ProviderType = Core.Domain.Enums.MailProviderType.Outlook
            };

            var tokenInformation = new TokenInformation()
            {
                AccountId = AccountId,
                Address = "bkaankose@outlook.com",
                ExpiresAt = DateTime.UtcNow.AddMinutes(-2),
                Id = Guid.NewGuid(),
                AccessToken = "emptyToken"
            };

            await databaseService.Connection.InsertOrReplaceAsync(tokenInformation);
            await databaseService.Connection.InsertOrReplaceAsync(Account);
            await databaseService.Connection.InsertOrReplaceAsync(preferences);
        }

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            var options = new SynchronizationOptions()
            {
                AccountId = AccountId,
                ProgressListener = this,
                Type = Core.Domain.Enums.SynchronizationType.Full
            };

            try
            {
                await Synchronizer.SynchronizeAsync(options, cancellationToken);
            }
            catch (Exception ex)
            {
                LogError("Synchronization failed.");
                LogError(ex.Message);

                if (ex.StackTrace != null)
                {
                    LogMessage(ex.StackTrace);
                }
            }
        }
    }
}
