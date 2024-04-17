namespace Wino.Core.Domain.Enums
{
    public enum SynchronizationType
    {
        FoldersOnly, // Only synchronize folder metadata.
        ExecuteRequests, // Run the queued requests, and then synchronize if needed.
        Inbox, // Only Inbox
        Custom, // Only sync folders that are specified in the options.
        Full, // Synchronize everything
    }
}
