namespace Wino.Core.Domain.Enums
{
    public enum SynchronizationType
    {
        FoldersOnly, // Only synchronize folder metadata.
        ExecuteRequests, // Run the queued requests, and then synchronize if needed.
        Inbox, // Only Inbox, Sent and Draft folders.
        Custom, // Only sync folders that are specified in the options.
        Full, // Synchronize all folders. This won't update profile or alias information.
        UpdateProfile, // Only update profile information
        Alias, // Only update alias information
    }
}
