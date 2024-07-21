using Wino.Domain.Entities;

namespace Wino.Domain.Interfaces
{
    public interface ITokenService
    {
        Task<TokenInformation> GetTokenInformationAsync(Guid accountId);
        Task SaveTokenInformationAsync(Guid accountId, TokenInformation tokenInformation);
    }
}
