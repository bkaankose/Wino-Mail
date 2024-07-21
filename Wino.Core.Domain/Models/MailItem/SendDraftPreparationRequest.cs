using MimeKit;
using Wino.Domain.Entities;

namespace Wino.Domain.Models.MailItem
{
    public record SendDraftPreparationRequest(MailCopy MailItem, MimeMessage Mime, MailItemFolder DraftFolder, MailItemFolder SentFolder, MailAccountPreferences AccountPreferences);
}
