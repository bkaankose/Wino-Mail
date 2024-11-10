using System;
using Microsoft.Identity.Client;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Core.Extensions
{
    public static class TokenizationExtensions
    {
        public static TokenInformation CreateTokenInformation(this AuthenticationResult clientBuilderResult)
        {
            // Plain access token info is not stored for Outlook in Wino's database.
            // Here we store UniqueId and Access Token in memory only to compare the UniqueId returned from MSAL auth result.

            var tokenInfo = new TokenInformation()
            {
                Address = clientBuilderResult.Account.Username,
                Id = Guid.NewGuid(),
                UniqueId = clientBuilderResult.UniqueId,
                AccessToken = clientBuilderResult.AccessToken
            };

            return tokenInfo;
        }
    }
}
