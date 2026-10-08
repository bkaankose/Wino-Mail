namespace Wino.Core.Domain.Enums;

public enum AccountCacheResetReason
{
    AccountRemoval,
    ExpiredCache,

    /// <summary>The mail mode was turned off for the account, so its downloaded messages were removed.</summary>
    MailAccessDisabled
}
