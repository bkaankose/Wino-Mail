using Wino.Core.Domain.Entities.Mail;

namespace Wino.Core.Domain.Interfaces;

public interface ICreateAccountAliasDialog
{
    public MailAccountAlias CreatedAccountAlias { get; set; }
}
