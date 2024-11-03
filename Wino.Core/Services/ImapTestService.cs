using System.IO;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Connectivity;
using Wino.Core.Integration;

namespace Wino.Core.Services
{
    public class ImapTestService : IImapTestService
    {
        public const string ProtocolLogFileName = "ImapProtocolLog.log";

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

            var logFile = Path.Combine(localAppFolderPath, ProtocolLogFileName);

            if (File.Exists(logFile))
                File.Delete(logFile);

            _protocolLogStream = File.Create(logFile);
        }

        public async Task TestImapConnectionAsync(CustomServerInformation serverInformation, bool allowSSLHandShake)
        {
            EnsureProtocolLogFileExists();

            var poolOptions = ImapClientPoolOptions.CreateTestPool(serverInformation, _protocolLogStream);

            var clientPool = new ImapClientPool(poolOptions)
            {
                ThrowOnSSLHandshakeCallback = !allowSSLHandShake
            };

            using (clientPool)
            {
                // This call will make sure that everything is authenticated + connected successfully.
                var client = await clientPool.GetClientAsync();

                clientPool.Release(client);
            }
        }
    }
}
