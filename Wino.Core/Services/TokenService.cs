using System;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities;

namespace Wino.Core.Services
{
    public interface ITokenService
    {
        Task<TokenInformation> GetTokenInformationAsync(Guid accountId);
        Task SaveTokenInformationAsync(Guid accountId, TokenInformation tokenInformation);
    }

    public class TokenService : BaseDatabaseService, ITokenService
    {
        public TokenService(IDatabaseService databaseService) : base(databaseService) { }

        public Task<TokenInformation> GetTokenInformationAsync(Guid accountId)
            => Connection.Table<TokenInformation>().FirstOrDefaultAsync(a => a.AccountId == accountId);

        public async Task SaveTokenInformationAsync(Guid accountId, TokenInformation tokenInformation)
        {
            // Delete all tokens for this account.
            await Connection.Table<TokenInformation>().DeleteAsync(a => a.AccountId == accountId);

            // Save new token info to the account.
            tokenInformation.AccountId = accountId;

            await Connection.InsertOrReplaceAsync(tokenInformation);
        }
    }
}
