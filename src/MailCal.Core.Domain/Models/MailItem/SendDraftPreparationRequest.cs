using System.Text.Json.Serialization;
using MimeKit;
using MailCal.Core.Domain.Entities.Mail;
using MailCal.Core.Domain.Entities.Shared;
using MailCal.Core.Domain.Extensions;

namespace MailCal.Core.Domain.Models.MailItem;

public record SendDraftPreparationRequest(MailCopy MailItem,
                                          MailAccountAlias SendingAlias,
                                          MailItemFolder SentFolder,
                                          MailItemFolder DraftFolder,
                                          MailAccountPreferences AccountPreferences,
                                          string Base64MimeMessage)
{
    [JsonIgnore]
    private MimeMessage mime;

    [JsonIgnore]
    public MimeMessage Mime => mime ??= Base64MimeMessage.GetMimeMessageFromBase64();
}
