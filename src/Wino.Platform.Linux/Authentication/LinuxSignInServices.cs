using Wino.Authentication;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Authentication;

namespace Wino.Platform.Linux.Authentication;

/// <summary>The one message every sign-in surface reports until Linux sign-in exists.</summary>
internal static class LinuxSignIn
{
    public const string Unavailable = "Interactive sign-in is not available on Linux yet.";

    public static NotSupportedException Exception() => new(Unavailable);
}

public sealed class LinuxGoogleAuthorizationCodeReceiver : IGoogleAuthorizationCodeReceiver
{
    public Task<GoogleAuthorizationCode> ReceiveCodeAsync(Func<Uri, string, Uri> authorizationUriFactory, CancellationToken cancellationToken)
    {
        return Task.FromException<GoogleAuthorizationCode>(LinuxSignIn.Exception());
    }
}
