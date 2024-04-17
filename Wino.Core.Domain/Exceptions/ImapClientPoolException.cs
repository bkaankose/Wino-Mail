using System;

namespace Wino.Core.Domain.Exceptions
{
    public class ImapClientPoolException : Exception
    {
        public ImapClientPoolException(Exception innerException) : base(Translator.Exception_ImapClientPoolFailed, innerException)
        {
        }
    }
}
