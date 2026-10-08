using System;

namespace Wino.Core.Domain.Exceptions;

public class SynchronizerEntityNotFoundException : Exception
{
    public SynchronizerEntityNotFoundException(string message) : base(message)
    {
    }
}
