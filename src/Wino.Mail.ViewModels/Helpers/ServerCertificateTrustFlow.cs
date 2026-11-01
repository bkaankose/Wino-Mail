#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Models.Connectivity;

namespace Wino.Mail.ViewModels.Helpers;

/// <summary>
/// The one way setup and server settings ask about a server certificate: refuse what cannot be trusted, show the
/// prompt, and keep an accepted certificate as a pending trust until the account is saved.
/// </summary>
internal static class ServerCertificateTrustFlow
{
    /// <summary>Throws with the message to show when the certificate cannot be trusted or the user declines it.</summary>
    public static async Task AcceptAsync(
        MailServerCertificateFailure? failure,
        string? failedReason,
        CustomServerInformation serverInformation,
        Guid accountId,
        Func<ServerCertificateTrustPrompt, Task<bool>> showPrompt)
    {
        if (failure == null)
            throw new InvalidOperationException(failedReason ?? Translator.IMAPSetupDialog_CertificateCannotBeTrusted);

        var prompt = ServerCertificateTrustPrompt.From(failure);
        if (!failure.CanTrust)
            throw new InvalidOperationException(prompt.BlockedMessage);

        if (!await showPrompt(prompt).ConfigureAwait(false))
            throw new InvalidOperationException(Translator.IMAPSetupDialog_CertificateDenied);

        serverInformation.PendingCertificateTrusts.RemoveAll(item =>
            item.Protocol == failure.Protocol &&
            string.Equals(item.Host, failure.Host, StringComparison.OrdinalIgnoreCase) &&
            item.Port == failure.Port);
        serverInformation.PendingCertificateTrusts.Add(failure.CreateTrust(accountId));
    }

    public static bool TryGetFailure(Exception exception, out MailServerCertificateFailure? failure)
    {
        failure = exception
            .GetInnerExceptions()
            .OfType<MailServerCertificateException>()
            .FirstOrDefault()?
            .Failure;

        return failure != null;
    }
}
