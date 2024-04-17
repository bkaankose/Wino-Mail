using System.Threading.Tasks;
using Wino.Core.Domain.Entities;
using Wino.Core.Domain.Enums;
using Wino.Core.Services;

namespace Wino.Core.Authenticators
{
    public abstract class BaseAuthenticator
    {
        public abstract MailProviderType ProviderType { get; }

        protected ITokenService TokenService { get; }

        protected BaseAuthenticator(ITokenService tokenService)
        {
            TokenService = tokenService;
        }

        internal Task SaveTokenInternalAsync(MailAccount account, TokenInformation tokenInformation)
            => TokenService.SaveTokenInformationAsync(account.Id, tokenInformation);
    }
}
