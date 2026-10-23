using MailCal.Core.Domain.Entities.Mail;

namespace MailCal.Core.Domain.Interfaces;

public interface ICreateAccountAliasDialog
{
    public MailAccountAlias CreatedAccountAlias { get; set; }
}
