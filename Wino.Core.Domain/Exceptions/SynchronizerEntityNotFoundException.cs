using System;

namespace Wino.Domain.Exceptions
{
    public class SynchronizerEntityNotFoundException : Exception
    {
        public SynchronizerEntityNotFoundException(string message) : base(message)
        {
        }
    }
}
