using System;

namespace Wino.Core.Domain.Exceptions;

public class CustomThemeCreationFailedException : Exception
{
    public CustomThemeCreationFailedException(string message) : base(message)
    {
    }
}
