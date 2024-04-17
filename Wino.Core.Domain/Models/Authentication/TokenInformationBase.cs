using System;

namespace Wino.Core.Domain.Models.Authentication
{
    public class TokenInformationBase
    {
        public string AccessToken { get; set; }
        public string RefreshToken { get; set; }

        /// <summary>
        /// UTC date for token expiration.
        /// </summary>
        public DateTime ExpiresAt { get; set; }

        /// <summary>
        /// Gets the value indicating whether the token is expired or not.
        /// </summary>
        public bool IsExpired => DateTime.UtcNow >= ExpiresAt;
    }
}
