# Outlook Queue-Based Synchronization Implementation

## Overview

This document describes the implementation of the queue-based, metadata-only synchronization system for Outlook, mirroring the approach used in Gmail but adapted for Outlook's per-folder synchronization model.

## Key Differences from Gmail

1. **Per-Folder Queue**: Unlike Gmail which uses per-account `InitialSynchronizationStatus`, Outlook uses per-folder queue tracking via `RemoteFolderId` in `MailItemQueue`.
2. **Folder-Level Processing**: Each folder maintains its own delta token and processes its queue independently.
3. **No Account-Level Status**: Outlook doesn't use `Account.InitialSynchronizationStatus` since sync is per-folder, not per-account.

## Architecture Changes

### 1. MailItemQueue Entity Enhancement

**File**: `Wino.Core.Domain\Entities\Mail\MailItemQueue.cs`

Added `RemoteFolderId` property to support per-folder queue tracking:

```csharp
public string RemoteFolderId { get; set; }  // For Outlook per-folder sync
```

### 2. Service Layer Updates

**Files**: 
- `Wino.Core.Domain\Interfaces\IMailService.cs`
- `Wino.Services\MailService.cs`
- `Wino.Core\Integration\Processors\DefaultChangeProcessor.cs`

Added new methods for folder-specific queue operations:

```csharp
Task<List<MailItemQueue>> GetMailItemQueueByFolderAsync(Guid accountId, string remoteFolderId, int take);
Task<int> GetMailItemQueueCountByFolderAsync(Guid accountId, string remoteFolderId);
```

### 3. OutlookSynchronizer Redesign

**File**: `Wino.Core\Synchronizers\OutlookSynchronizer.cs`

#### New Methods

1. **QueueMailIdsForFolderAsync**
   - Queues all mail IDs for a specific folder using Delta API
   - Only retrieves message IDs (minimal data transfer)
   - Stores delta token for future incremental syncs
   - Creates queue entries with `RemoteFolderId` for folder tracking

2. **ProcessMailQueueForFolderAsync**
   - Processes queued mail IDs in batches
   - Downloads metadata only (no MIME content)
   - Handles failures with retry logic
   - Updates queue item status (IsProcessed, FailedCount)

3. **DownloadMessageMetadataBatchAsync**
   - Downloads metadata for a batch of messages concurrently
   - Uses semaphore to limit concurrent downloads (10 max)
   - Calls `CreateMailCopyFromMessage` for metadata extraction
   - Creates `NewMailItemPackage` with null MimeMessage

4. **CreateMailCopyFromMessage** _(Centralized)_
   - **REPLACES** scattered `AsMailCopy()` and `CreateMinimalMailCopyAsync()` calls
   - Single source of truth for converting Graph Message to MailCopy
   - Extracts all required fields from metadata
   - Sets FolderId, UniqueId, and FileId

#### Modified Methods

1. **DownloadMailsForInitialSyncAsync**
   - Now orchestrates queue-based sync
   - Step 1: Queue all mail IDs via Delta API
   - Step 2: Process queue in batches

2. **ProcessDeltaChangesAndDownloadMailsAsync**
   - Downloads delta changes with metadata only
   - Uses `DownloadMessageMetadataBatchAsync` instead of full MIME download

3. **CreateNewMailPackagesAsync**
   - Still downloads MIME for specific scenarios (search results, drafts)
   - Uses `CreateMailCopyFromMessage` for consistency
   - Not called during normal sync operations

#### Removed Methods

- `DownloadMailsConcurrentlyAsync` - Replaced by queue system
- `DownloadSingleMailAsync` - Replaced by queue batch processing
- Scattered `CreateMinimalMailCopyAsync` implementations

## Synchronization Flow

### Initial Sync (Per Folder)

```
1. SynchronizeFolderAsync
   ├─ Check: !folder.IsInitialSyncCompleted
   └─ DownloadMailsForInitialSyncAsync
      ├─ QueueMailIdsForFolderAsync
      │  ├─ Use Delta API with Select=["Id"]
      │  ├─ Iterate all pages
      │  ├─ Create MailItemQueue entries with RemoteFolderId
      │  └─ Store delta token
      └─ ProcessMailQueueForFolderAsync
         ├─ Get queue items by folder (100 at a time)
         ├─ Process in chunks of 20
         ├─ DownloadMessageMetadataBatchAsync
         │  ├─ Concurrent download (10 max)
         │  ├─ GetMessageByIdAsync (metadata fields only)
         │  ├─ CreateMailCopyFromMessage
         │  └─ CreateMailAsync (package with null MIME)
         └─ Update queue status
```

### Delta Sync (Per Folder)

```
1. SynchronizeFolderAsync
   ├─ Check: folder.IsInitialSyncCompleted
   └─ ProcessDeltaChangesAndDownloadMailsAsync
      ├─ Use Delta API with existing token
      ├─ Collect new mail IDs
      ├─ DownloadMessageMetadataBatchAsync
      │  ├─ Download metadata only
      │  └─ Create MailCopy entries
      └─ Update delta token
```

### On-Demand MIME Download

```
User Reads Mail
└─ DownloadMissingMimeMessageAsync
   ├─ Download full MIME via /messages/{id}/$value
   └─ SaveMimeFileAsync
```

## Benefits

1. **Reduced Bandwidth**: Only metadata downloaded during sync (no 50+ MB MIME files)
2. **Faster Sync**: Parallel processing with controlled concurrency
3. **Resilient**: Queue system handles failures gracefully with retry logic
4. **Consistent**: Centralized `CreateMailCopyFromMessage` method
5. **Scalable**: Per-folder processing allows independent folder syncs

## Code Consolidation

### Before (Scattered Approach)

```csharp
// Multiple places creating MailCopy
var mailCopy = message.AsMailCopy();
mailCopy.FolderId = folder.Id;
// ... repeat in 3+ locations

// Mixed MIME downloading
var package = await CreateNewMailPackagesAsync(...); // Downloads MIME
var minimal = await CreateMinimalMailCopyAsync(...); // No MIME
```

### After (Centralized Approach)

```csharp
// Single method for all scenarios
var mailCopy = CreateMailCopyFromMessage(message, folder);

// Clear separation
// Sync: metadata only
var package = new NewMailItemPackage(mailCopy, null, folder.RemoteFolderId);

// On-demand: full MIME
await DownloadMissingMimeMessageAsync(mailCopy, ...);
```

## Graph API Fields Used

Only essential fields are requested during sync:

```csharp
private readonly string[] outlookMessageSelectParameters =
[
    "InferenceClassification",
    "Flag",
    "Importance",
    "IsRead",
    "IsDraft",
    "ReceivedDateTime",
    "HasAttachments",
    "BodyPreview",
    "Id",
    "ConversationId",
    "From",
    "Subject",
    "ParentFolderId",
    "InternetMessageId",
];
```

**NOT downloaded during sync**:
- Body content (HTML/Text)
- Raw MIME message
- Attachment content
- Extended properties

## Migration Path for IMAP (Future)

The base `WinoSynchronizer` class has been updated with:

```csharp
protected virtual Task QueueMailIdsForInitialSyncAsync(MailItemFolder folder, CancellationToken cancellationToken = default);
protected virtual Task<List<string>> DownloadMailsFromQueueAsync(MailItemFolder folder, int batchSize, CancellationToken cancellationToken = default);
protected virtual Task<MailCopy> CreateMinimalMailCopyAsync(TMessageType message, MailItemFolder assignedFolder, CancellationToken cancellationToken = default);
```

IMAP can override these methods to implement similar queue-based sync:
- TODO: Add folder-based queue support to IMAP
- TODO: Implement metadata-only header parsing
- TODO: Centralize IMAP MailCopy creation

## Testing Checklist

- [x] Initial sync queues all mail IDs per folder
- [x] Queue processing downloads metadata only
- [x] Delta sync uses metadata-only approach
- [x] Failed queue items retry correctly
- [x] Concurrent download respects semaphore limits
- [x] Delta token stored and used correctly per folder
- [ ] Search results still download MIME when needed
- [ ] Draft handling works with MIME headers
- [ ] On-demand MIME download functions correctly
- [ ] Large folders (1000+ messages) sync efficiently
- [ ] Network interruption recovery

## Performance Expectations

### Before (With MIME):
- 100 messages ≈ 100-500 MB download
- Sync time: 5-10 minutes
- API calls: 100+ individual message downloads

### After (Metadata Only):
- 100 messages ≈ 1-5 MB download (metadata)
- Sync time: 30-60 seconds
- API calls: Batched requests (10-20 concurrent)
- MIME downloaded only when user reads (lazy loading)

## Notes

- `CreateNewMailPackagesAsync` is now marked as **only for special cases**
- `DefaultChangeProcessor` no longer needed for basic operations
- All synchronizers can benefit from this pattern (Gmail, IMAP, Outlook)
- The `InitialSyncMimeDownloadCount` property is now obsolete
