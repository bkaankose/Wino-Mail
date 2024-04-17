using MimeKit;
using Wino.Core.Domain.Entities;

namespace Wino.Core.Domain.Models.MailItem
{
    public record SendDraftPreparationRequest(MailCopy MailItem, MimeMessage Mime, MailItemFolder DraftFolder, MailItemFolder SentFolder, MailAccountPreferences AccountPreferences);
}
