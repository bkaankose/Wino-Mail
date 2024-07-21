using System;

namespace Wino.Domain.Exceptions
{
    public class CustomThemeCreationFailedException : Exception
    {
        public CustomThemeCreationFailedException(string message) : base(message)
        {
        }
    }
}
