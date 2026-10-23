using MailCal.Core.Domain.Entities.Mail;
using MailCal.Core.Domain.Entities.Shared;

namespace MailCal.Messaging.UI;

public record DraftCreated(MailCopy DraftMail, MailAccount Account) : UIMessageBase<DraftCreated>;
