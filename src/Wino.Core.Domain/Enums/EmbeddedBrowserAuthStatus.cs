namespace Wino.Core.Domain.Enums;

public enum EmbeddedBrowserAuthStatus
{
    /// <summary>The embedded browser reached the redirect address; the result carries it.</summary>
    Success,

    /// <summary>The user closed the sign-in surface.</summary>
    Canceled,

    /// <summary>The sign-in surface could not be shown or its browser could not start.</summary>
    Failed
}
