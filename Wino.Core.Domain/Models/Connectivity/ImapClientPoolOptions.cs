using System.IO;
using Wino.Core.Domain.Entities;

namespace Wino.Core.Domain.Models.Connectivity
{
    public class ImapClientPoolOptions
    {
        public Stream ProtocolLog { get; }
        public CustomServerInformation ServerInformation { get; }
        public bool IsTestPool { get; }

        protected ImapClientPoolOptions(CustomServerInformation serverInformation, Stream protocolLog, bool isTestPool)
        {
            ServerInformation = serverInformation;
            ProtocolLog = protocolLog;
            IsTestPool = isTestPool;
        }

        public static ImapClientPoolOptions CreateDefault(CustomServerInformation serverInformation, Stream protocolLog)
            => new(serverInformation, protocolLog, false);

        public static ImapClientPoolOptions CreateTestPool(CustomServerInformation serverInformation, Stream protocolLog)
            => new(serverInformation, protocolLog, true);
    }
}
