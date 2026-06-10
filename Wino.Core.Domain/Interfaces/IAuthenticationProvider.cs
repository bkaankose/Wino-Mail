using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Interfaces;

public interface IAuthenticationProvider
{
    IAuthenticator GetAuthenticator(MailProviderType providerType);

    /// <summary>
    /// Sets the window handle used to parent interactive authentication UI (the WAM broker
    /// dialog). The handle may belong to another process - the broker runs out-of-process
    /// and only needs it for ownership/positioning. Pass 0 to clear.
    /// </summary>
    void SetInteractiveAuthorizationWindow(long parentWindowHandle);
}
