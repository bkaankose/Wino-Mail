using MailCal.Core.Domain.Entities.Mail;
using MailCal.Core.Domain.Entities.Shared;

namespace MailCal.Messaging.UI;

public record DraftFailed(MailCopy DraftMail, MailAccount Account) : UIMessageBase<DraftFailed>;
