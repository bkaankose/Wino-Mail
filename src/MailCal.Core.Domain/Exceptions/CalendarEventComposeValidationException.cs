using System;

namespace MailCal.Core.Domain.Exceptions;

public sealed class CalendarEventComposeValidationException : Exception
{
    public CalendarEventComposeValidationException(string message) : base(message)
    {
    }
}
