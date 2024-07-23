using System;

namespace Wino.Core.Domain.Exceptions
{
    [Serializable]
    public class WinoServerException : Exception
    {
        public WinoServerException(Exception innerException) : base(Translator.Exception_WinoServerException, innerException) { }
    }
}
