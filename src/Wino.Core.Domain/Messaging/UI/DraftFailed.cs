using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Core.Domain.Messaging.UI;

public record DraftFailed(MailCopy DraftMail, MailAccount Account) : UIMessageBase<DraftFailed>;
