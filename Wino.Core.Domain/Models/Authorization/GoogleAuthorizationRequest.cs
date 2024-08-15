using System;
using System.Collections.Generic;
using System.Linq;
using Wino.Core.Domain.Exceptions;

namespace Wino.Core.Domain.Models.Authorization
{
    public class GoogleAuthorizationRequest
    {
        public const string RedirectUri = "google.pw.oauth2:/oauth2redirect";

        const string authorizationEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
        const string CodeChallangeMethod = "S256";

        public GoogleAuthorizationRequest(string state, string codeVerifier, string codeChallange)
        {
            State = state;
            CodeVerifier = codeVerifier;
            CodeChallange = codeChallange;
        }

        // Pre
        public string State { get; set; }
        public string CodeVerifier { get; set; }
        public string CodeChallange { get; set; }
        public string ClientId { get; set; }

        // Post
        public string AuthorizationCode { get; set; }

        public string BuildRequest(string clientId)
        {
            ClientId = clientId;

            // Creates the OAuth 2.0 authorization request.
            return string.Format("{0}?response_type=code&scope=https://mail.google.com/ https://www.googleapis.com/auth/gmail.labels https://www.googleapis.com/auth/userinfo.profile&redirect_uri={1}&client_id={2}&state={3}&code_challenge={4}&code_challenge_method={5}",
                authorizationEndpoint,
                Uri.EscapeDataString(RedirectUri),
                ClientId,
                State,
                CodeChallange,
                CodeChallangeMethod);
        }

        public void ValidateAuthorizationCode(Uri callbackUri)
        {
            if (callbackUri == null)
                throw new GoogleAuthenticationException(Translator.Exception_GoogleAuthCallbackNull);

            string queryString = callbackUri.Query;

            Dictionary<string, string> queryStringParams = queryString.Substring(1).Split('&').ToDictionary(c => c.Split('=')[0], c => Uri.UnescapeDataString(c.Split('=')[1]));

            if (queryStringParams.ContainsKey("error"))
                throw new GoogleAuthenticationException(string.Format(Translator.Exception_GoogleAuthError, queryStringParams["error"]));

            if (!queryStringParams.ContainsKey("code") || !queryStringParams.ContainsKey("state"))
                throw new GoogleAuthenticationException(Translator.Exception_GoogleAuthCorruptedCode + queryString);

            // Gets the Authorization code & state
            string code = queryStringParams["code"];
            string incomingState = queryStringParams["state"];

            // Compares the receieved state to the expected value, to ensure that
            // this app made the request which resulted in authorization
            if (incomingState != State)
                throw new GoogleAuthenticationException(string.Format(Translator.Exception_GoogleAuthInvalidResponse, incomingState));

            AuthorizationCode = code;
        }
    }
}
