using System;

namespace MailCal.Core.Domain.Exceptions;

public class SynchronizerEntityNotFoundException : Exception
{
    public SynchronizerEntityNotFoundException(string message) : base(message)
    {
    }
}
