namespace Wino.Core.Domain.Enums;

/// <summary>
/// Optional provider-backed features that require OAuth permissions beyond the
/// account's core mail and calendar permissions.
/// </summary>
public enum ProviderFeature
{
    MailFilters = 1,

    /// <summary>
    /// Moving mail from another account into this one. Outlook adds the copy over Exchange Online IMAP, a separate
    /// resource from Graph that is authorized with its own token request.
    /// </summary>
    CrossAccountMoveImport = 2
}

public enum ProviderFeatureAuthorizationState
{
    Active = 0,
    ReauthorizationRequired = 1
}
