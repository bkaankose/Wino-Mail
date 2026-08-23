namespace Wino.Core.Domain.Models.Accounts;

/// <summary>
/// Encapsulates the profile information of an account.
/// </summary>
/// <param name="SenderName">Display sender name for the account.</param>
/// <param name="ProfilePicture">Provider profile-picture fetch outcome.</param>
/// <param name="AccountAddress">Address of the profile.</param>
public record ProfileInformation(string SenderName, ProfilePictureFetchResult ProfilePicture, string AccountAddress);
