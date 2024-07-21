using System;
using Wino.Domain.Models.AutoDiscovery;

namespace Wino.Domain.Exceptions
{
    public class ImapConnectionFailedPackage
    {
        public ImapConnectionFailedPackage(Exception error, string protocolLog, AutoDiscoverySettings settings)
        {
            Error = error;
            ProtocolLog = protocolLog;
            Settings = settings;
        }

        public AutoDiscoverySettings Settings { get; }
        public Exception Error { get; }
        public string ProtocolLog { get; }

        public string GetErrorMessage() => Error.InnerException == null ? Error.Message : Error.InnerException.Message;
    }
}
