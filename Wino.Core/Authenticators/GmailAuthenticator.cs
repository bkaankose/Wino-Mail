using System;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Authentication;
using Wino.Core.Domain.Models.Authorization;
using Wino.Core.Services;

namespace Wino.Core.Authenticators
{
    public class GmailAuthenticator : BaseAuthenticator, IGmailAuthenticator
    {
        public string ClientId { get; } = "973025879644-s7b4ur9p3rlgop6a22u7iuptdc0brnrn.apps.googleusercontent.com";

        private const string TokenEndpoint = "https://www.googleapis.com/oauth2/v4/token";
        private const string RefreshTokenEndpoint = "https://oauth2.googleapis.com/token";
        private const string UserInfoEndpoint = "https://gmail.googleapis.com/gmail/v1/users/me/profile";

        public override MailProviderType ProviderType => MailProviderType.Gmail;

        private readonly INativeAppService _nativeAppService;

        public GmailAuthenticator(ITokenService tokenService, INativeAppService nativeAppService) : base(tokenService)
        {
            _nativeAppService = nativeAppService;
        }

        /// <summary>
        /// Performs tokenization code exchange and retrieves the actual Access - Refresh tokens from Google
        /// after redirect uri returns from browser.
        /// </summary>
        /// <param name="tokenizationRequest">Tokenization request.</param>
        /// <exception cref="GoogleAuthenticationException">In case of network or parsing related error.</exception>
        private async Task<TokenInformation> PerformCodeExchangeAsync(GoogleTokenizationRequest tokenizationRequest)
        {
            var uri = tokenizationRequest.BuildRequest();

            var content = new StringContent(uri, Encoding.UTF8, "application/x-www-form-urlencoded");

            var handler = new HttpClientHandler()
            {
                AllowAutoRedirect = true
            };

            var client = new HttpClient(handler);

            var response = await client.PostAsync(TokenEndpoint, content);
            string responseString = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                throw new GoogleAuthenticationException(Translator.Exception_GoogleAuthorizationCodeExchangeFailed);

            var parsed = JsonNode.Parse(responseString).AsObject();

            if (parsed.ContainsKey("error"))
                throw new GoogleAuthenticationException(parsed["error"]["message"].GetValue<string>());

            var accessToken = parsed["access_token"].GetValue<string>();
            var refreshToken = parsed["refresh_token"].GetValue<string>();
            var expiresIn = parsed["expires_in"].GetValue<long>();

            var expirationDate = DateTime.UtcNow.AddSeconds(expiresIn);

            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

            // Get basic user info for UserName.

            var userinfoResponse = await client.GetAsync(UserInfoEndpoint);
            string userinfoResponseContent = await userinfoResponse.Content.ReadAsStringAsync();

            var parsedUserInfo = JsonNode.Parse(userinfoResponseContent).AsObject();

            if (parsedUserInfo.ContainsKey("error"))
                throw new GoogleAuthenticationException(parsedUserInfo["error"]["message"].GetValue<string>());

            var username = parsedUserInfo["emailAddress"].GetValue<string>();

            return new TokenInformation()
            {
                Id = Guid.NewGuid(),
                Address = username,
                AccessToken = accessToken,
                RefreshToken = refreshToken,
                ExpiresAt = expirationDate
            };
        }

        public async Task<TokenInformation> GetTokenAsync(MailAccount account)
        {
            var cachedToken = await TokenService.GetTokenInformationAsync(account.Id)
                ?? throw new AuthenticationAttentionException(account);

            if (cachedToken.IsExpired)
            {
                // Refresh token with new exchanges.
                // No need to check Username for account.

                var refreshedTokenInfoBase = await RefreshTokenAsync(cachedToken.RefreshToken);

                cachedToken.RefreshTokens(refreshedTokenInfoBase);

                // Save new token and return.
                await SaveTokenInternalAsync(account, cachedToken);
            }

            return cachedToken;
        }


        public async Task<TokenInformation> GenerateTokenAsync(MailAccount account, bool saveToken)
        {
            var authRequest = _nativeAppService.GetGoogleAuthorizationRequest();

            var authorizationUri = authRequest.BuildRequest(ClientId);

            Uri responseRedirectUri = null;

            try
            {
                //await _authorizationCompletionSource.Task.WaitAsync(_authorizationCancellationTokenSource.Token);
                responseRedirectUri = await _nativeAppService.GetAuthorizationResponseUriAsync(this, authorizationUri);
            }
            catch (Exception)
            {
                throw new AuthenticationException(Translator.Exception_AuthenticationCanceled);
            }

            authRequest.ValidateAuthorizationCode(responseRedirectUri);

            // Start tokenization.
            var tokenizationRequest = new GoogleTokenizationRequest(authRequest);
            var tokenInformation = await PerformCodeExchangeAsync(tokenizationRequest);

            if (saveToken)
            {
                await SaveTokenInternalAsync(account, tokenInformation);
            }

            return tokenInformation;
        }

        /// <summary>
        /// Internally exchanges refresh token with a new access token and returns new TokenInformation.
        /// </summary>
        /// <param name="refresh_token">Token to be used in refreshing.</param>
        /// <returns>New TokenInformationBase that has new tokens and expiration date without a username. This token is not saved to database after returned.</returns>
        private async Task<TokenInformationBase> RefreshTokenAsync(string refresh_token)
        {
            // TODO: This doesn't work.
            var refreshUri = string.Format("client_id={0}&refresh_token={1}&grant_type=refresh_token", ClientId, refresh_token);

            //Uri.EscapeDataString(refreshUri);
            var content = new StringContent(refreshUri, Encoding.UTF8, "application/x-www-form-urlencoded");

            var client = new HttpClient();

            var response = await client.PostAsync(RefreshTokenEndpoint, content);

            string responseString = await response.Content.ReadAsStringAsync();

            var parsed = JsonNode.Parse(responseString).AsObject();

            // TODO: Error parsing is incorrect.
            if (parsed.ContainsKey("error"))
                throw new GoogleAuthenticationException(parsed["error_description"].GetValue<string>());

            var accessToken = parsed["access_token"].GetValue<string>();

            string activeRefreshToken = refresh_token;

            // Refresh token might not be returned.
            // In this case older refresh token is still available for new refreshes.
            // Only change if provided.

            if (parsed.ContainsKey("refresh_token"))
            {
                activeRefreshToken = parsed["refresh_token"].GetValue<string>();
            }

            var expiresIn = parsed["expires_in"].GetValue<long>();
            var expirationDate = DateTime.UtcNow.AddSeconds(expiresIn);

            return new TokenInformationBase()
            {
                AccessToken = accessToken,
                ExpiresAt = expirationDate,
                RefreshToken = activeRefreshToken
            };
        }
    }
}
