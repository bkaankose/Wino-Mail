using System.IO;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Integration;

namespace Wino.Core.Services
{
    public class ImapTestService : IImapTestService
    {
        public const string ProtocolLogFileName = "ImapProtocolLog.log";

        private readonly IPreferencesService _preferencesService;
        private readonly IAppInitializerService _appInitializerService;

        private Stream _protocolLogStream;

        public ImapTestService(IPreferencesService preferencesService, IAppInitializerService appInitializerService)
        {
            _preferencesService = preferencesService;
            _appInitializerService = appInitializerService;
        }

        private void EnsureProtocolLogFileExists()
        {
            // Create new file for protocol logger.
            var localAppFolderPath = _appInitializerService.GetApplicationDataFolder();

            var logFile = Path.Combine(localAppFolderPath, ProtocolLogFileName);

            if (File.Exists(logFile))
                File.Delete(logFile);

            _protocolLogStream = File.Create(logFile);
        }

        public async Task TestImapConnectionAsync(CustomServerInformation serverInformation)
        {
            EnsureProtocolLogFileExists();

            var clientPool = new ImapClientPool(serverInformation, _protocolLogStream);

            using (clientPool)
            {
                // This call will make sure that everything is authenticated + connected successfully.
                var client = await clientPool.GetClientAsync();

                clientPool.Release(client);
            }
        }
    }
}
