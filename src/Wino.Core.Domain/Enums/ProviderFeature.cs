namespace Wino.Core.Domain.Enums;

/// <summary>
/// Optional provider-backed features that require OAuth permissions beyond the
/// account's core mail and calendar permissions.
/// </summary>
public enum ProviderFeature
{
    MailFilters = 1
}

public enum ProviderFeatureAuthorizationState
{
    Active = 0,
    ReauthorizationRequired = 1
}
