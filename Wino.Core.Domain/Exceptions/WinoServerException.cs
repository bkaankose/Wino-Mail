using System;

namespace Wino.Core.Domain.Exceptions
{
    /// <summary>
    /// All server crash types. Wino Server ideally should not throw anything else than this Exception type.
    /// </summary>
    public class WinoServerException : Exception
    {
        public WinoServerException(string message) : base(message) { }
    }
}
