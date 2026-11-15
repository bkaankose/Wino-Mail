#nullable enable
using System;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Exceptions;

public sealed class OAuthSignInException : Exception
{
    public OAuthSignInException(OAuthSignInFailureKind kind, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }

    public OAuthSignInFailureKind Kind { get; }
}

/// <summary>
/// The stored grant can no longer be used (invalid_grant, invalid_client, unauthorized_client, or no tokens).
/// Only interactive sign-in fixes it.
/// </summary>
public sealed class OAuthGrantInvalidException : Exception
{
    public OAuthGrantInvalidException(Guid key, string? oauthError, bool isConfigurationError)
        : base($"The OAuth grant for {key} is no longer valid ({oauthError ?? "no tokens"}).")
    {
        Key = key;
        OAuthError = oauthError;
        IsConfigurationError = isConfigurationError;
    }

    public Guid Key { get; }
    public string? OAuthError { get; }
    public bool IsConfigurationError { get; }
}

/// <summary>The issuer could not be reached or answered without an OAuth error. Retry later; nothing is wrong with the grant.</summary>
public sealed class OAuthTransientException : Exception
{
    public OAuthTransientException(string message, Exception? innerException = null) : base(message, innerException) { }
}
