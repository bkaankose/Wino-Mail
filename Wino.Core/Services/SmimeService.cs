using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using MimeKit;
using MimeKit.Cryptography;
using Serilog;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Reader;

namespace Wino.Core.Services;

/// <summary>
/// Companion-side S/MIME processor. Owns every cryptographic operation of the mail
/// pipeline; the UI process only ships thumbprints and renders the files produced here.
/// Requires a registered SecureMimeContext (see CryptographyContext.Register in the
/// companion Program).
/// </summary>
public class SmimeService : ISmimeService
{
    private const string ProcessedMimeFileName = "smime-processed.eml";

    private static readonly ILogger Logger = Log.ForContext<SmimeService>();

    private readonly IMimeFileService _mimeFileService;

    public SmimeService(IMimeFileService mimeFileService)
    {
        _mimeFileService = mimeFileService;
    }

    public async Task<SmimeRenderInfo> PrepareSmimeRenderAsync(Guid fileId, Guid accountId)
    {
        var mimeMessageInformation = await _mimeFileService.GetMimeMessageInformationAsync(fileId, accountId).ConfigureAwait(false);
        var message = mimeMessageInformation.MimeMessage;

        var signatures = new List<SmimeSignatureInfo>();
        var isEncrypted = false;
        var isSigned = false;

        var body = message.Body;

        // Messages can stack layers (e.g. signed-then-encrypted); unwrap until the body
        // is a plain entity.
        while (true)
        {
            if (body is MultipartSigned multipartSigned)
            {
                CollectSignatures(signatures, () => multipartSigned.Verify());
                isSigned = true;
                body = multipartSigned[0];
            }
            else if (body is ApplicationPkcs7Mime { SecureMimeType: SecureMimeType.EnvelopedData } envelopedPart)
            {
                body = envelopedPart.Decrypt();
                isEncrypted = true;
            }
            else if (body is ApplicationPkcs7Mime { SecureMimeType: SecureMimeType.SignedData } signedPart)
            {
                MimeEntity extracted = null;
                CollectSignatures(signatures, () => signedPart.Verify(out extracted));
                isSigned = true;

                if (extracted == null)
                    break;

                body = extracted;
            }
            else
            {
                break;
            }
        }

        if (!isEncrypted && !isSigned)
            return SmimeRenderInfo.NotProtected;

        message.Body = body;

        var processedFilePath = Path.Combine(mimeMessageInformation.Path, ProcessedMimeFileName);

        // Overwrite a previous run completely; the file is a derived cache.
        await using (var fileStream = File.Create(processedFilePath))
        {
            await message.WriteToAsync(fileStream).ConfigureAwait(false);
        }

        return new SmimeRenderInfo(isEncrypted, isSigned, ProcessedMimeFileName, signatures);
    }

    public Task<string> ApplyDraftSecurityAsync(string base64MimeMessage, bool sign, bool encrypt, string signingCertificateThumbprint)
    {
        if (!sign && !encrypt)
            return Task.FromResult(base64MimeMessage);

        using var sourceStream = new MemoryStream(Convert.FromBase64String(base64MimeMessage));
        var message = MimeMessage.Load(sourceStream);

        if (sign)
        {
            var signingCertificate = FindSigningCertificate(signingCertificateThumbprint)
                ?? throw new InvalidOperationException($"S/MIME signing certificate with thumbprint '{signingCertificateThumbprint}' was not found in the user certificate store.");

            var signer = new CmsSigner(signingCertificate) { DigestAlgorithm = DigestAlgorithm.Sha1 };

            message.Body = encrypt
                ? ApplicationPkcs7Mime.SignAndEncrypt(signer, BuildRecipients(message), message.Body)
                : ApplicationPkcs7Mime.Sign(signer, message.Body);
        }
        else
        {
            // Encrypt only; the registered SecureMimeContext resolves recipient
            // certificates from the user stores by mailbox address.
            message.Body = ApplicationPkcs7Mime.Encrypt(message.To.Mailboxes, message.Body);
        }

        using var outputStream = new MemoryStream();
        message.WriteTo(FormatOptions.Default, outputStream);

        return Task.FromResult(Convert.ToBase64String(outputStream.ToArray()));
    }

    private static void CollectSignatures(List<SmimeSignatureInfo> signatures, Func<DigitalSignatureCollection> verify)
    {
        DigitalSignatureCollection digitalSignatures;

        try
        {
            digitalSignatures = verify();
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "S/MIME signature verification failed for the message.");
            return;
        }

        foreach (var signature in digitalSignatures)
        {
            bool isValid;

            try
            {
                isValid = signature.Verify();
            }
            catch (DigitalSignatureVerifyException)
            {
                isValid = false;
            }

            signatures.Add(new SmimeSignatureInfo(
                signature.SignerCertificate?.Name,
                signature.SignerCertificate?.Fingerprint,
                signature.SignerCertificate?.CreationDate ?? default,
                signature.SignerCertificate?.ExpirationDate ?? default,
                isValid));
        }
    }

    private static X509Certificate2 FindSigningCertificate(string thumbprint)
    {
        if (string.IsNullOrWhiteSpace(thumbprint))
            return null;

        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);

        return store.Certificates
            .Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false)
            .OfType<X509Certificate2>()
            .FirstOrDefault();
    }

    private static CmsRecipientCollection BuildRecipients(MimeMessage message)
    {
        var recipients = new CmsRecipientCollection();

        foreach (var mailbox in message.To.Mailboxes)
        {
            var certificate = FindRecipientCertificate(mailbox.Address)
                ?? throw new InvalidOperationException($"No S/MIME encryption certificate found for recipient '{mailbox.Address}'.");

            recipients.Add(new CmsRecipient(certificate));
        }

        return recipients;
    }

    // Mirrors the UI-side SmimeCertificateService lookup: Wino-imported certificates are
    // tagged with this friendly name and matched by subject.
    private const string CertificateFriendlyName = "Wino Mail Certificate";

    private static X509Certificate2 FindRecipientCertificate(string emailAddress)
        => FindWinoCertificate(StoreName.My, emailAddress) ?? FindWinoCertificate(StoreName.AddressBook, emailAddress);

    private static X509Certificate2 FindWinoCertificate(StoreName storeName, string emailAddress)
    {
        try
        {
            using var store = new X509Store(storeName, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadOnly);

            return store.Certificates.FirstOrDefault(c =>
                c.FriendlyName == CertificateFriendlyName &&
                c.Subject.Contains(emailAddress, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Certificate store {StoreName} lookup failed for {EmailAddress}", storeName, emailAddress);
            return null;
        }
    }
}
