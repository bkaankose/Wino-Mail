namespace Wino.Core.Domain.Models.Authentication;

/// <summary>
/// Previously known as TokenInformation.
/// We used to store this model in the database.
/// Now we store it in the memory.
/// </summary>
/// <param name="AccessToken">Access token/</param>
/// <param name="AccountAddress">Mailbox address of the authenticated user.</param>
/// <param name="AuthenticationAddress">Provider account address used for token cache lookup.</param>
/// <param name="IssuingClientId">Outlook only: the Entra app that issued the token.</param>
public record TokenInformationEx(string AccessToken, string AccountAddress, string AuthenticationAddress = null, string IssuingClientId = null);
