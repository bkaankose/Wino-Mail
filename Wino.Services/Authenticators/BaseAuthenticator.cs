using System.Threading.Tasks;
using Wino.Domain.Entities;
using Wino.Domain.Enums;
using Wino.Domain.Interfaces;

namespace Wino.Services.Authenticators
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
