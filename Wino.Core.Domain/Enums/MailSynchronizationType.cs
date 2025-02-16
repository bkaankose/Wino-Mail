﻿namespace Wino.Core.Domain.Enums;

public enum MailSynchronizationType
{
    UpdateProfile, // Only update profile information
    ExecuteRequests, // Run the queued requests, and then synchronize if needed.
    FoldersOnly, // Only synchronize folder metadata.
    InboxOnly, // Only Inbox, Sent, Draft and Deleted folders.
    CustomFolders, // Only sync folders that are specified in the options.
    FullFolders, // Synchronize all folders. This won't update profile or alias information.
    Alias, // Only update alias information
    IMAPIdle // Idle client triggered synchronization.
}
