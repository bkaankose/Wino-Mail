using System;

namespace Wino.Core.Domain.Exceptions;

public class MimePersistenceException : Exception
{
    public MimePersistenceException(string message) : base(message) { }
}
