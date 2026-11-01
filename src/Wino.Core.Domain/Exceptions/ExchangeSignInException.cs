#nullable enable
using System;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Exceptions;

/// <summary>An interactive Exchange sign-in failed. <see cref="Exception.Message"/> is translated for the user.</summary>
public sealed class ExchangeSignInException : Exception
{
    public ExchangeSignInException(ExchangeSignInFailureKind kind, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }

    public ExchangeSignInFailureKind Kind { get; }
}
