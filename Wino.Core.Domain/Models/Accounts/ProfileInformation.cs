namespace Wino.Core.Domain.Models.Accounts;

/// <summary>
/// Encapsulates the profile information of an account.
/// </summary>
/// <param name="SenderName">Display sender name for the account.</param>
/// <param name="Base64ProfilePictureData">Base 64 encoded profile picture data of the account. Thumbnail size.</param>
/// <param name="AccountAddress">Address of the profile.</param>
public record ProfileInformation(string SenderName, string Base64ProfilePictureData, string AccountAddress);
