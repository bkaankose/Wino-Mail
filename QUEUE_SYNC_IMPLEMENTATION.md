# Mail Synchronization Queue-Based Implementation

This document summarizes the changes made to implement the new queue-based mail synchronization system for Wino Mail.

## Overview

The new system changes the mail synchronization approach from downloading everything immediately during initial sync to queuing mail IDs first and then downloading mail content progressively. This makes initial synchronization much more efficient and responsive.

## Changes Made

### 1. New Database Entity: MailItemQueue

**File:** `Wino.Core.Domain/Entities/Mail/MailItemQueue.cs`

Created a new table to store mail IDs that need to be downloaded from the server:
- `Id`: Primary key (auto-increment)
- `AccountId`: Account that owns the mail
- `FolderId`: Local folder ID
- `RemoteFolderId`: Server-specific folder ID
- `MailCopyId`: Mail ID from the remote server
- `QueuedDate`: When the item was queued
- `Priority`: Priority for processing (lower number = higher priority)

### 2. Enhanced MailItemFolder Entity

**File:** `Wino.Core.Domain/Entities/Mail/MailItemFolder.cs`

Added new property:
- `IsInitialSyncCompleted`: Boolean flag to track whether initial mail ID synchronization is complete for the folder

### 3. New Queue Management Service

**Files:** 
- `Wino.Core.Domain/Interfaces/IMailItemQueueService.cs`
- `Wino.Services/MailItemQueueService.cs`

Created a comprehensive service for managing the mail queue with methods to:
- Queue mail items for download
- Get next batch of items to process
- Remove processed items from queue
- Check queue counts and existence
- Clear queue for folders/accounts

### 4. Updated Database Service

**File:** `Wino.Services/DatabaseService.cs`

Added `MailItemQueue` to the database table creation list.

### 5. Enhanced Base Synchronizer

**File:** `Wino.Core/Synchronizers/WinoSynchronizer.cs`

Added new virtual methods that synchronizers can override to support queue-based sync:
- `QueueMailIdsForInitialSyncAsync()`: Queue all mail IDs for initial sync
- `DownloadMailsFromQueueAsync()`: Download mails from queue in batches  
- `CreateMinimalMailCopyAsync()`: Create MailCopy with minimal properties (no MIME download)

### 6. OutlookSynchronizer Implementation

**File:** `Wino.Core/Synchronizers/OutlookSynchronizer.cs`

Major changes to implement the new synchronization logic:

#### Constructor Changes
- Added `IMailItemQueueService` dependency injection

#### New Synchronization Algorithm
The `SynchronizeFolderAsync` method now implements the new algorithm:

1. **Check Initial Sync Status**: If `IsInitialSyncCompleted` is false:
   - Clear existing queue items for the folder
   - Queue all mail IDs using `QueueMailIdsForInitialSyncAsync()`
   - Mark initial sync as completed

2. **Process Queue**: Download mails from queue in batches (50 at a time):
   - Get queued items for the folder
   - Download each mail with minimal properties (no MIME)
   - Create MailCopy objects with essential fields only
   - Remove processed items from queue

3. **Process Delta Changes**: Handle incremental changes using existing delta sync logic

#### New Methods Implemented
- `QueueMailIdsForInitialSyncAsync()`: Uses PageIterator to efficiently get all message IDs
- `CreateMinimalMailCopyAsync()`: Creates MailCopy without downloading MIME content
- `GetMessageByIdAsync()`: Downloads individual messages with selected properties only
- `ProcessDeltaChangesAsync()`: Handles incremental sync with delta tokens

### 7. Enhanced Change Processor Interface

**File:** `Wino.Core/Integration/Processors/DefaultChangeProcessor.cs`

Added new method to `IOutlookChangeProcessor`:
- `UpdateFolderInitialSyncCompletedAsync()`: Updates the initial sync completion status

**File:** `Wino.Core/Integration/Processors/OutlookChangeProcessor.cs`

Implemented the new method to update the `IsInitialSyncCompleted` field in the database.

### 8. Dependency Injection Updates

**Files:**
- `Wino.Core/Services/SynchronizerFactory.cs`: Added `IMailItemQueueService` dependency and updated OutlookSynchronizer creation
- `Wino.Services/ServicesContainerSetup.cs`: Registered `IMailItemQueueService` as transient service

## Key Benefits

1. **Faster Initial Sync**: Only mail IDs are downloaded initially, making the sync much faster
2. **Progressive Loading**: Mail content is downloaded progressively based on queue
3. **Better User Experience**: Users see folder structure and mail list faster
4. **Efficient Resource Usage**: Avoids downloading full MIME messages during initial sync
5. **Prioritization Support**: Queue system supports priority-based processing
6. **Resilient**: Can handle sync interruptions and resume from where it left off

## Implementation Details

### Queue-Based Processing
- Initial sync: Download only message IDs and queue them
- Progressive download: Process queue items in batches
- Minimal properties: Download only essential mail properties (Subject, Preview, etc.)
- No MIME download: Full MIME messages are not downloaded during initial sync

### Delta Sync Integration
- Existing delta sync logic is preserved for incremental changes
- Delta tokens are properly managed and updated
- Expired tokens trigger queue reset and re-sync

### Error Handling
- Robust error handling for individual mail downloads
- Failed items don't block the entire batch
- Proper logging for debugging and monitoring

## Future Enhancements

The architecture is designed to be easily extended to other synchronizers:
- Gmail and IMAP synchronizers can adopt the same pattern
- Common functionality is in the base `WinoSynchronizer` class
- Queue service is provider-agnostic

## Testing Recommendations

1. Test initial synchronization with large mailboxes
2. Verify progressive loading of mail content
3. Test interruption and resume scenarios
4. Validate delta sync functionality
5. Test with multiple accounts and folders
6. Verify queue management operations
7. Test error scenarios and recovery

This implementation significantly improves the initial synchronization experience while maintaining all existing functionality for incremental syncing.