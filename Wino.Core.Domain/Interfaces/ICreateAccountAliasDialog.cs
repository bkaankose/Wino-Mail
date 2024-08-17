using Wino.Core.Domain.Entities;

namespace Wino.Core.Domain.Interfaces
{
    public interface ICreateAccountAliasDialog
    {
        public MailAccountAlias CreatedAccountAlias { get; set; }
    }
}
