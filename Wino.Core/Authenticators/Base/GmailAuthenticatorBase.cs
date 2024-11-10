using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Services;

namespace Wino.Core.Authenticators.Base
{
    public abstract class GmailAuthenticatorBase : BaseAuthenticator, IGmailAuthenticator
    {
        protected GmailAuthenticatorBase(ITokenService tokenService) : base(tokenService)
        {
        }

        public abstract string ClientId { get; }
        public bool ProposeCopyAuthURL { get; set; }

        public abstract Task<TokenInformation> GenerateTokenAsync(MailAccount account, bool saveToken);

        public abstract Task<TokenInformation> GetTokenAsync(MailAccount account);
    }
}
