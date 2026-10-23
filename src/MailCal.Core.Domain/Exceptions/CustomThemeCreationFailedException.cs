using System;

namespace MailCal.Core.Domain.Exceptions;

public class CustomThemeCreationFailedException : Exception
{
    public CustomThemeCreationFailedException(string message) : base(message)
    {
    }
}
