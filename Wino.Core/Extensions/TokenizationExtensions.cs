using System;
using Microsoft.Identity.Client;
using Wino.Core.Domain.Entities;

namespace Wino.Core.Extensions
{
    public static class TokenizationExtensions
    {
        public static TokenInformation CreateTokenInformation(this AuthenticationResult clientBuilderResult)
        {
            var expirationDate = clientBuilderResult.ExpiresOn.UtcDateTime;
            var accesToken = clientBuilderResult.AccessToken;
            var userName = clientBuilderResult.Account.Username;

            // MSAL does not expose refresh token for security reasons.
            // This token info will be created without refresh token.
            // but OutlookIntegrator will ask for publicApplication to refresh it
            // in case of expiration.

            var tokenInfo = new TokenInformation()
            {
                ExpiresAt = expirationDate,
                AccessToken = accesToken,
                Address = userName,
                Id = Guid.NewGuid(),
            };

            return tokenInfo;
        }
    }
}
