using System.IO;
using System.Threading.Tasks;
using MailKit;
using MailKit.Net.Imap;
using Wino.Core.Domain.Entities;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Services
{
    public class ImapTestService : IImapTestService
    {
        public const string ProtocolLogFileName = "ImapProtocolLog.log";

        private readonly IPreferencesService _preferencesService;
        private readonly IAppInitializerService _appInitializerService;

        public ImapTestService(IPreferencesService preferencesService, IAppInitializerService appInitializerService)
        {
            _preferencesService = preferencesService;
            _appInitializerService = appInitializerService;
        }

        public async Task TestImapConnectionAsync(CustomServerInformation serverInformation)
        {
            ImapClient client = null;

            if (_preferencesService.IsMailkitProtocolLoggerEnabled)
            {
                // Create new file for protocol logger.

                var localAppFolderPath = _appInitializerService.GetApplicationDataFolder();

                var logFile = Path.Combine(localAppFolderPath, ProtocolLogFileName);

                if (File.Exists(logFile))
                    File.Delete(logFile);

                var stream = File.Create(logFile);

                client = new ImapClient(new ProtocolLogger(stream));
            }
            else
                client = new ImapClient();

            using (client)
            {
                // todo: test connection
                // await client.InitializeAsync(serverInformation);
                await client.DisconnectAsync(true);
            }
        }
    }
}
