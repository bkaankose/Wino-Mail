using System;
using SQLite;
using Wino.Core.Domain.Models.Authentication;

namespace Wino.Core.Domain.Entities
{
    public class TokenInformation : TokenInformationBase
    {
        [PrimaryKey]
        public Guid Id { get; set; }

        public Guid AccountId { get; set; }

        public string Address { get; set; }

        public void RefreshTokens(TokenInformationBase tokenInformationBase)
        {
            if (tokenInformationBase == null)
                throw new ArgumentNullException(nameof(tokenInformationBase));

            AccessToken = tokenInformationBase.AccessToken;
            RefreshToken = tokenInformationBase.RefreshToken;
            ExpiresAt = tokenInformationBase.ExpiresAt;
        }
    }
}
