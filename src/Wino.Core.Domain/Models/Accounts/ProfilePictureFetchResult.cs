namespace Wino.Core.Domain.Models.Accounts;

public enum ProfilePictureFetchStatus
{
    Downloaded,
    ConfirmedAbsent,
    FetchFailed
}

/// <summary>
/// Distinguishes a downloaded provider image from a confirmed absence and a transient fetch failure.
/// </summary>
public sealed record ProfilePictureFetchResult(ProfilePictureFetchStatus Status, byte[] ImageData = null)
{
    public static ProfilePictureFetchResult Downloaded(byte[] imageData) => new(ProfilePictureFetchStatus.Downloaded, imageData);
    public static ProfilePictureFetchResult ConfirmedAbsent { get; } = new(ProfilePictureFetchStatus.ConfirmedAbsent);
    public static ProfilePictureFetchResult FetchFailed { get; } = new(ProfilePictureFetchStatus.FetchFailed);
}
