namespace Wino.Core.Domain.Models.Authentication;

/// <summary>
/// Previously known as TokenInformation.
/// We used to store this model in the database.
/// Now we store it in the memory.
/// </summary>
/// <param name="AccessToken">Access token/</param>
/// <param name="AccountAddress">Address of the authenticated user.</param>
public record TokenInformationEx(string AccessToken, string AccountAddress);
