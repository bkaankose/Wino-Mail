using System.Threading.Tasks;

using Wino.Core.Domain.Entities;

namespace Wino.Core.Domain.Interfaces
{
    public interface IOutlookAuthenticator : IAuthenticator { }
    public interface IGmailAuthenticator : IAuthenticator
    {
        bool ProposeCopyAuthURL { get; set; }
        Task<TokenInformation> RefreshTokenAsync(MailAccount account);
    }
    public interface IImapAuthenticator : IAuthenticator { }
}
