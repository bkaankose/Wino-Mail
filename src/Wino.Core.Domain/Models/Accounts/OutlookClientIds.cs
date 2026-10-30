#nullable enable
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Core.Domain.Models.Accounts;

/// <summary>
/// The Microsoft Entra apps Outlook accounts sign in with. New accounts use <see cref="Current"/>. On Windows, accounts
/// without a stored client id were created with <see cref="Legacy"/> and stay on it forever; never migrate them, and
/// never delete the legacy app: the API uses its secret for Store collections.
/// </summary>
public static class OutlookClientIds
{
    public const string Legacy = "b19c2035-d740-49ff-b297-de6ec561b208";
    public const string Current = "497b333d-c689-4b27-8f59-7dace624111a";

    /// <summary>
    /// The client an account signs in with. Accounts without a stored client id use <paramref name="legacyClientId"/>
    /// when the platform has one, otherwise <see cref="Current"/>.
    /// </summary>
    public static string Resolve(MailAccount? account, string? legacyClientId)
    {
        if (!string.IsNullOrWhiteSpace(account?.OutlookClientId))
            return account.OutlookClientId;

        return account is null || string.IsNullOrWhiteSpace(legacyClientId) ? Current : legacyClientId;
    }
}
