using System;

namespace Wino.Core.Domain.Exceptions;

public sealed class CalendarEventComposeValidationException : Exception
{
    public CalendarEventComposeValidationException(string message) : base(message)
    {
    }
}
