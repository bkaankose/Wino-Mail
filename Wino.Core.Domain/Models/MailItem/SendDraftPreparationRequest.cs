using System.Text.Json.Serialization;
using MimeKit;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Extensions;

namespace Wino.Core.Domain.Models.MailItem;

/// <summary>
/// Send request prepared in the UI. S/MIME protection is described by flags only: the
/// companion applies the actual signing/encryption (see ISmimeService) before queueing,
/// so no cryptography runs in the UI process.
/// </summary>
public record SendDraftPreparationRequest(MailCopy MailItem,
                                          MailAccountAlias SendingAlias,
                                          MailItemFolder SentFolder,
                                          MailItemFolder DraftFolder,
                                          MailAccountPreferences AccountPreferences,
                                          string Base64MimeMessage,
                                          bool SmimeSign = false,
                                          bool SmimeEncrypt = false,
                                          string SmimeSigningCertificateThumbprint = null)
{
    [JsonIgnore]
    private MimeMessage mime;

    [JsonIgnore]
    public MimeMessage Mime => mime ??= Base64MimeMessage.GetMimeMessageFromBase64();
}
