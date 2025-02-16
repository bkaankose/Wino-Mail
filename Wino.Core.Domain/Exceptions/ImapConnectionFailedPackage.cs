using Wino.Core.Domain.Models.AutoDiscovery;

namespace Wino.Core.Domain.Exceptions
{
    public class ImapConnectionFailedPackage
    {
        public ImapConnectionFailedPackage(string errorMessage, string protocolLog, AutoDiscoverySettings settings)
        {
            ErrorMessage = errorMessage;
            ProtocolLog = protocolLog;
            Settings = settings;
        }

        public AutoDiscoverySettings Settings { get; }
        public string ErrorMessage { get; set; }
        public string ProtocolLog { get; }
    }
}
