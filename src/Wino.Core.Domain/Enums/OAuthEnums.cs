namespace Wino.Core.Domain.Enums;

public enum OAuthRedirectKind
{
    CustomScheme,
    Loopback
}

/// <summary>Where a profile came from. Decides how strictly its endpoints are checked.</summary>
public enum OAuthTrustSource
{
    BuiltIn,
    Discovered,
    UserEntered
}

public enum OAuthSaslMechanism
{
    OAuthBearer,
    XOAuth2
}

public enum OAuthSignInFailureKind
{
    Cancelled,
    Denied,
    AnotherSignInInProgress,
    TimedOut,
    Failed
}

public enum DavServiceKind
{
    CalDav,
    CardDav
}
