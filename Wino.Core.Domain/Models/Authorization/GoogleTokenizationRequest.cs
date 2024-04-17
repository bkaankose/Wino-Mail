using System;
using Wino.Core.Domain.Exceptions;

namespace Wino.Core.Domain.Models.Authorization
{
    public class GoogleTokenizationRequest
    {
        public GoogleTokenizationRequest(GoogleAuthorizationRequest authorizationRequest)
        {
            if (authorizationRequest == null)
                throw new GoogleAuthenticationException("Authorization request is empty.");

            AuthorizationRequest = authorizationRequest;

            if (string.IsNullOrEmpty(AuthorizationRequest.AuthorizationCode))
                throw new GoogleAuthenticationException("Authorization request has empty code.");
        }

        public GoogleAuthorizationRequest AuthorizationRequest { get; set; }

        public string BuildRequest()
        {
            return string.Format("code={0}&redirect_uri={1}&client_id={2}&code_verifier={3}&scope=&grant_type=authorization_code",
                AuthorizationRequest.AuthorizationCode, Uri.EscapeDataString(GoogleAuthorizationRequest.RedirectUri), AuthorizationRequest.ClientId, AuthorizationRequest.CodeVerifier);
        }
    }
}
