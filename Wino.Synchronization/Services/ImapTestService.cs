using System.IO;
using System.Threading.Tasks;
using Wino.Core.Integration;
using Wino.Domain;
using Wino.Domain.Entities;
using Wino.Domain.Interfaces;

namespace Wino.Core.Services
{
    public class ImapTestService : IImapTestService
    {
        private readonly IPreferencesService _preferencesService;
        private readonly IApplicationConfiguration _appInitializerService;

        private Stream _protocolLogStream;

        public ImapTestService(IPreferencesService preferencesService, IApplicationConfiguration appInitializerService)
        {
            _preferencesService = preferencesService;
            _appInitializerService = appInitializerService;
        }

        private void EnsureProtocolLogFileExists()
        {
            // Create new file for protocol logger.
            var localAppFolderPath = _appInitializerService.ApplicationDataFolderPath;

            var logFile = Path.Combine(localAppFolderPath, Constants.ProtocolLogFileName);

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
