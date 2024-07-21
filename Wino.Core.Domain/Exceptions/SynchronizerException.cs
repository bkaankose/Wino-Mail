using System;

namespace Wino.Domain.Exceptions
{
    public class SynchronizerException : Exception
    {
        public SynchronizerException(string message) : base(message)
        {
        }

        public SynchronizerException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}
