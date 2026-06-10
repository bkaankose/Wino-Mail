using System;
using System.Threading.Tasks;
using Wino.Core.Domain.Models.Reader;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// S/MIME cryptography for the mail pipeline. All cryptographic operations run in the
/// background companion process - the UI never touches BouncyCastle or the registered
/// SecureMimeContext. Certificates are resolved from the CurrentUser stores, which both
/// processes share (same user); only thumbprints and verification results cross the pipe.
/// </summary>
[Wino.Core.Domain.Attributes.WinoRpcService]
public interface ISmimeService
{
    /// <summary>
    /// Decrypts and/or verifies an S/MIME protected message and writes the extracted inner
    /// MIME next to the original in the shared MIME storage so the UI can render it without
    /// running cryptography. Returns <see cref="SmimeRenderInfo.NotProtected"/> when the
    /// message carries no S/MIME layers.
    /// </summary>
    /// <param name="fileId">MIME file id of the message.</param>
    /// <param name="accountId">Owning account.</param>
    Task<SmimeRenderInfo> PrepareSmimeRenderAsync(Guid fileId, Guid accountId);

    /// <summary>
    /// Applies S/MIME signing and/or encryption to an outgoing message. Companion-internal:
    /// called by the request delegator while preparing a send; never crosses the pipe.
    /// </summary>
    /// <param name="base64MimeMessage">The unprotected message, base64 encoded.</param>
    /// <param name="sign">Sign with the certificate identified by <paramref name="signingCertificateThumbprint"/>.</param>
    /// <param name="encrypt">Encrypt to all To recipients (certificates resolved from user stores).</param>
    /// <param name="signingCertificateThumbprint">Thumbprint of the signing certificate in the CurrentUser My store.</param>
    /// <returns>The protected message, base64 encoded.</returns>
    [Wino.Core.Domain.Attributes.WinoRpcExclude]
    Task<string> ApplyDraftSecurityAsync(string base64MimeMessage, bool sign, bool encrypt, string signingCertificateThumbprint);
}
