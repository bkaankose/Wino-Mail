using System;

namespace Wino.Core.Domain.Exceptions
{
    public class ImapClientPoolException : Exception
    {
        public ImapClientPoolException(Exception innerException, string protocolLog) : base(Translator.Exception_ImapClientPoolFailed, innerException)
        {
            ProtocolLog = protocolLog;
        }

        public string ProtocolLog { get; }
    }
}
