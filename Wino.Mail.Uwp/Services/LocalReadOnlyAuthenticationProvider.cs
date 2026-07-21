using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.Uwp.Services;

/// <summary>
/// Satisfies dependencies of locally constructed database services without bringing
/// authentication into the UWP process. Hybrid proxies route every authentication or
/// mutation path to the companion before this provider can be used.
/// </summary>
internal sealed class LocalReadOnlyAuthenticationProvider : IAuthenticationProvider
{
    public IAuthenticator GetAuthenticator(MailProviderType providerType) =>
        throw new InvalidOperationException(
            $"Authentication for '{providerType}' is companion-only and cannot run in the UWP read process.");
}
