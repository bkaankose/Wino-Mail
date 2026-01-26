using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using MailKit;
using MailKit.Net.Imap;
using MoreLinq;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Requests.Bundles;
using Wino.Core.Requests.Calendar;
using Wino.Core.Requests.Folder;
using Wino.Core.Requests.Mail;
using Wino.Messaging.UI;

namespace Wino.Core.Synchronizers;

public abstract class WinoSynchronizer<TBaseRequest, TMessageType, TCalendarEventType> : BaseSynchronizer<TBaseRequest>, IWinoSynchronizerBase    where TBaseRequest : class{
    protected bool IsDisposing { get; private set; }

    protected Dictionary<MailSynchronizationOptions, CancellationTokenSource> PendingSynchronizationRequest = new();

    protected ILogger Logger = Log.ForContext<WinoSynchronizer<TBaseRequest, TMessageType, TCalendarEventType>>();

    protected WinoSynchronizer(MailAccount account, IMessenger messenger) : base(account, messenger) { }

    /// <summary>
    /// How many items per single HTTP call can be modified.
    /// </summary>
    public abstract uint BatchModificationSize { get; }

    /// <summary>
    /// How many items must be downloaded per folder when the folder is first synchronized.
    /// Only metadata is downloaded during sync - MIME content is fetched on-demand when user reads mail.
    /// </summary>
    public abstract uint InitialMessageDownloadCountPerFolder { get; }

    /// <summary>
    /// Creates a new Wino Mail Item package out of native message type with metadata only.
    /// NO MIME content is downloaded during synchronization - only headers and essential metadata.
    /// MIME will be downloaded on-demand when user explicitly reads the message.
    /// </summary>
    /// <param name="message">Native message type for the synchronizer.</param>
    /// <param name="assignedFolder">Folder to assign the mail to.</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Package with MailCopy metadata. MimeMessage will be null during sync.</returns>
    public abstract Task<List<NewMailItemPackage>> CreateNewMailPackagesAsync(TMessageType message, MailItemFolder assignedFolder, CancellationToken cancellationToken = default);

    /// <summary>
    /// Refreshes the aliases of the account.
    /// Only available for Gmail right now.
    /// </summary>
    protected virtual Task SynchronizeAliasesAsync() => Task.CompletedTask;

    /// <summary>
    /// Queues all mail ids for initial synchronization for a specific folder.
    /// Only overridden by synchronizers that support the new queue-based sync.
    /// </summary>
    /// <param name="folder">Folder to queue mail ids for</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task</returns>
    protected virtual Task QueueMailIdsForInitialSyncAsync(MailItemFolder folder, CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>
    /// Downloads mail items from the queue in batches.
    /// Only overridden by synchronizers that support the new queue-based sync.
    /// </summary>
    /// <param name="folder">Folder to download mails for</param>
    /// <param name="batchSize">Number of items to download in each batch</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of downloaded mail ids</returns>
    protected virtual Task<List<string>> DownloadMailsFromQueueAsync(MailItemFolder folder, int batchSize, CancellationToken cancellationToken = default) => Task.FromResult(new List<string>());

    /// <summary>
    /// Creates a MailCopy object with minimal properties from the native message type.
    /// This is used during synchronization to create mail entries WITHOUT downloading MIME content.
    /// Only metadata (headers, labels, flags) is extracted from the native message format.
    /// MIME content will be downloaded later on-demand when user reads the message.
    /// Only overridden by synchronizers that support metadata-only synchronization.
    /// </summary>
    /// <param name="message">Native message type</param>
    /// <param name="assignedFolder">Folder this message belongs to</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>MailCopy with minimal properties populated from metadata</returns>
    protected virtual Task<MailCopy> CreateMinimalMailCopyAsync(TMessageType message, MailItemFolder assignedFolder, CancellationToken cancellationToken = default) => Task.FromResult<MailCopy>(null);

    /// <summary>
    /// Internally synchronizes the account's mails with the given options.
    /// Not exposed and overriden for each synchronizer.
    /// </summary>
    /// <param name="options">Synchronization options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Synchronization result that contains summary of the sync.</returns>
    protected abstract Task<MailSynchronizationResult> SynchronizeMailsInternalAsync(MailSynchronizationOptions options, CancellationToken cancellationToken = default);

    /// <summary>
    /// Internally synchronizes the events of the account with given options.
    /// Not exposed and overriden for each synchronizer.
    /// </summary>
    /// <param name="options">Synchronization options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Synchronization result that contains summary of the sync.</returns>
    protected abstract Task<CalendarSynchronizationResult> SynchronizeCalendarEventsInternalAsync(CalendarSynchronizationOptions options, CancellationToken cancellationToken = default);

    /// <summary>
    /// Batches network requests, executes them, and does the needed synchronization after the batch request execution.
    /// </summary>
    /// <param name="options">Synchronization options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Synchronization result that contains summary of the sync.</returns>
    public async Task<MailSynchronizationResult> SynchronizeMailsAsync(MailSynchronizationOptions options, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!ShouldQueueMailSynchronization(options))
            {
                Log.Debug($"{options.Type} synchronization is ignored.");
                return MailSynchronizationResult.Canceled;
            }

            var newCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            PendingSynchronizationRequest.Add(options, newCancellationTokenSource);
            activeSynchronizationCancellationToken = newCancellationTokenSource.Token;

            // ImapSynchronizer will send this type when an Idle client receives a notification of changes.
            // We should not execute requests in this case.
            bool shouldExecuteRequests = options.Type != MailSynchronizationType.IMAPIdle;

            bool shouldDelayExecution = false;
            int maxExecutionDelay = 0;

            if (shouldExecuteRequests && changeRequestQueue.Any())
            {
                State = AccountSynchronizerState.ExecutingRequests;

                List<IExecutableRequest> nativeRequests = new();

                List<IRequestBase> requestCopies = new(changeRequestQueue);

                var keys = changeRequestQueue.GroupBy(a => a.GroupingKey());

                foreach (var group in keys)
                {
                    var key = group.Key;

                    if (key is MailSynchronizerOperation mailSynchronizerOperation)
                    {
                        switch (mailSynchronizerOperation)
                        {
                            case MailSynchronizerOperation.MarkRead:
                                nativeRequests.AddRange(MarkRead(new BatchMarkReadRequest(group.Cast<MarkReadRequest>())));
                                break;
                            case MailSynchronizerOperation.Move:
                                nativeRequests.AddRange(Move(new BatchMoveRequest(group.Cast<MoveRequest>())));
                                break;
                            case MailSynchronizerOperation.Delete:
                                nativeRequests.AddRange(Delete(new BatchDeleteRequest(group.Cast<DeleteRequest>())));
                                break;
                            case MailSynchronizerOperation.CreateDraft:
                                nativeRequests.AddRange(CreateDraft(group.ElementAt(0) as CreateDraftRequest));
                                break;
                            case MailSynchronizerOperation.Send:
                                nativeRequests.AddRange(SendDraft(group.ElementAt(0) as SendDraftRequest));
                                break;
                            case MailSynchronizerOperation.ChangeFlag:
                                nativeRequests.AddRange(ChangeFlag(new BatchChangeFlagRequest(group.Cast<ChangeFlagRequest>())));
                                break;
                            case MailSynchronizerOperation.AlwaysMoveTo:
                                nativeRequests.AddRange(AlwaysMoveTo(new BatchAlwaysMoveToRequest(group.Cast<AlwaysMoveToRequest>())));
                                break;
                            case MailSynchronizerOperation.MoveToFocused:
                                nativeRequests.AddRange(MoveToFocused(new BatchMoveToFocusedRequest(group.Cast<MoveToFocusedRequest>())));
                                break;
                            case MailSynchronizerOperation.Archive:
                                nativeRequests.AddRange(Archive(new BatchArchiveRequest(group.Cast<ArchiveRequest>())));
                                break;
                            default:
                                break;
                        }
                    }
                    else if (key is FolderSynchronizerOperation folderSynchronizerOperation)
                    {
                        switch (folderSynchronizerOperation)
                        {
                            case FolderSynchronizerOperation.RenameFolder:
                                nativeRequests.AddRange(RenameFolder(group.ElementAt(0) as RenameFolderRequest));
                                break;
                            case FolderSynchronizerOperation.EmptyFolder:
                                nativeRequests.AddRange(EmptyFolder(group.ElementAt(0) as EmptyFolderRequest));
                                break;
                            case FolderSynchronizerOperation.MarkFolderRead:
                                nativeRequests.AddRange(MarkFolderAsRead(group.ElementAt(0) as MarkFolderAsReadRequest));
                                break;
                            default:
                                break;
                        }
                    }
                }

                changeRequestQueue.Clear();

                Console.WriteLine($"Prepared {nativeRequests.Count()} native requests");

                await ExecuteNativeRequestsAsync(nativeRequests, activeSynchronizationCancellationToken).ConfigureAwait(false);

                PublishUnreadItemChanges();

                // Execute request sync options should be re-calculated after execution.
                // This is the part we decide which individual folders must be synchronized
                // after the batch request execution.
                if (options.Type == MailSynchronizationType.ExecuteRequests)
                    options = GetSynchronizationOptionsAfterRequestExecution(requestCopies, options.Id);

                // Let servers to finish their job. Sometimes the servers doesn't respond immediately.
                // Bug: if Outlook can't create the message in Sent Items folder before this delay,
                // message will not appear in user's inbox since it's not in the Sent Items folder.

                shouldDelayExecution =
                    (Account.ProviderType == MailProviderType.Outlook)
                    && requestCopies.Any(a => a.ResynchronizationDelay > 0);

                if (shouldDelayExecution)
                {
                    maxExecutionDelay = requestCopies.Aggregate(0, (max, next) => Math.Max(max, next.ResynchronizationDelay));
                }

                // In terms of flag/read changes, there is no point of synchronizing must have folders.
                options.ExcludeMustHaveFolders = requestCopies.All(a => a is ICustomFolderSynchronizationRequest request && request.ExcludeMustHaveFolders);
            }

            await synchronizationSemaphore.WaitAsync(activeSynchronizationCancellationToken);

            // Set indeterminate progress for initial state
            UpdateSyncProgress(0, 0, "Synchronizing...");

            State = AccountSynchronizerState.Synchronizing;

            // Handle special synchronization types.

            // Profile information sync.
            if (options.Type == MailSynchronizationType.UpdateProfile)
            {
                if (!Account.IsProfileInfoSyncSupported) return MailSynchronizationResult.Empty;

                ProfileInformation newProfileInformation = null;

                try
                {
                    newProfileInformation = await SynchronizeProfileInformationInternalAsync();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to update profile information for {Name}", Account.Name);

                    return MailSynchronizationResult.Failed(ex);
                }

                return MailSynchronizationResult.Completed(newProfileInformation);
            }

            // Alias sync.
            if (options.Type == MailSynchronizationType.Alias)
            {
                if (!Account.IsAliasSyncSupported) return MailSynchronizationResult.Empty;

                try
                {
                    await SynchronizeAliasesAsync();

                    return MailSynchronizationResult.Empty;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to update aliases for {Name}", Account.Name);

                    return MailSynchronizationResult.Failed(ex);
                }
            }

            if (shouldDelayExecution)
            {
                await Task.Delay(maxExecutionDelay);
            }

            // Start the internal synchronization.
            var synchronizationResult = await SynchronizeMailsInternalAsync(options, activeSynchronizationCancellationToken).ConfigureAwait(false);

            PublishUnreadItemChanges();

            return synchronizationResult;
        }
        catch (OperationCanceledException)
        {
            Logger.Warning("Synchronization canceled.");

            return MailSynchronizationResult.Canceled;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Synchronization failed for {Name}", Account.Name);

            throw;
        }
        finally
        {
            // Find the request and remove it from the pending list.

            var pendingRequest = PendingSynchronizationRequest.FirstOrDefault(a => a.Key.Id == options.Id);

            if (pendingRequest.Key != null)
            {
                PendingSynchronizationRequest.Remove(pendingRequest.Key);
            }

            // Reset synchronization progress
            ResetSyncProgress();

            State = AccountSynchronizerState.Idle;
            synchronizationSemaphore.Release();
        }
    }

    /// <summary>
    /// Batches network requests, executes them, and does the needed synchronization after the batch request execution.
    /// </summary>
    /// <param name="options">Synchronization options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Synchronization result that contains summary of the sync.</returns>
    public async Task<CalendarSynchronizationResult> SynchronizeCalendarEventsAsync(CalendarSynchronizationOptions options, CancellationToken cancellationToken = default)
    {
        bool shouldExecuteRequests = changeRequestQueue.Any(r => r is ICalendarActionRequest);
        bool shouldDelayExecution = false;
        int maxExecutionDelay = 0;

        if (shouldExecuteRequests)
        {
            State = AccountSynchronizerState.ExecutingRequests;

            List<IExecutableRequest> nativeRequests = new();
            List<IRequestBase> requestCopies = new(changeRequestQueue.Where(r => r is ICalendarActionRequest));

            var keys = requestCopies.GroupBy(a => a.GroupingKey());

            foreach (var group in keys)
            {
                var key = group.Key;

                if (key is CalendarSynchronizerOperation calendarSynchronizerOperation)
                {
                    switch (calendarSynchronizerOperation)
                    {
                        case CalendarSynchronizerOperation.CreateEvent:
                            nativeRequests.AddRange(CreateCalendarEvent(group.ElementAt(0) as CreateCalendarEventRequest));
                            break;
                        case CalendarSynchronizerOperation.AcceptEvent:
                            nativeRequests.AddRange(AcceptEvent(group.ElementAt(0) as AcceptEventRequest));
                            break;
                        case CalendarSynchronizerOperation.DeclineEvent:
                            if (Account.ProviderType == MailProviderType.Outlook)
                            {
                                nativeRequests.AddRange(OutlookDeclineEvent(group.ElementAt(0) as OutlookDeclineEventRequest));
                            }
                            else
                            {
                                nativeRequests.AddRange(DeclineEvent(group.ElementAt(0) as DeclineEventRequest));
                            }
                            break;
                        case CalendarSynchronizerOperation.TentativeEvent:
                            nativeRequests.AddRange(TentativeEvent(group.ElementAt(0) as TentativeEventRequest));
                            break;
                        case CalendarSynchronizerOperation.UpdateEvent:
                            nativeRequests.AddRange(UpdateCalendarEvent(group.ElementAt(0) as UpdateCalendarEventRequest));
                            break;
                        case CalendarSynchronizerOperation.DeleteEvent:
                            nativeRequests.AddRange(DeleteCalendarEvent(group.ElementAt(0) as DeleteCalendarEventRequest));
                            break;
                        default:
                            break;
                    }
                }
            }

            // Remove processed calendar requests from queue
            changeRequestQueue.RemoveAll(r => r is ICalendarActionRequest);

            Console.WriteLine($"Prepared {nativeRequests.Count()} native calendar requests");

            await ExecuteNativeRequestsAsync(nativeRequests, cancellationToken).ConfigureAwait(false);

            // Let servers to finish their job. Sometimes the servers don't respond immediately.
            shouldDelayExecution = requestCopies.Any(a => a.ResynchronizationDelay > 0);

            if (shouldDelayExecution)
            {
                maxExecutionDelay = requestCopies.Aggregate(0, (max, next) => Math.Max(max, next.ResynchronizationDelay));
            }
        }

        if (shouldDelayExecution)
        {
            await Task.Delay(maxExecutionDelay, cancellationToken);
        }

        // Execute the actual synchronization
        return await SynchronizeCalendarEventsInternalAsync(options, cancellationToken);
    }

    /// <summary>
    /// Updates unread item counts for some folders and account.
    /// Sends a message that shell can pick up and update the UI.
    /// </summary>
    private void PublishUnreadItemChanges()
        => WeakReferenceMessenger.Default.Send(new RefreshUnreadCountsMessage(Account.Id));

    /// <summary>
    /// Attempts to find out the best possible synchronization options after the batch request execution.
    /// </summary>
    /// <param name="batches">Batch requests to run in synchronization.</param>
    /// <returns>New synchronization options with minimal HTTP effort.</returns>
    private MailSynchronizationOptions GetSynchronizationOptionsAfterRequestExecution(List<IRequestBase> requests, Guid existingSynchronizationId)
    {
        List<Guid> synchronizationFolderIds = requests
                .Where(a => a is ICustomFolderSynchronizationRequest)
                .Cast<ICustomFolderSynchronizationRequest>()
                .SelectMany(a => a.SynchronizationFolderIds)
                .ToList();

        var options = new MailSynchronizationOptions()
        {
            AccountId = Account.Id,
        };

        options.Id = existingSynchronizationId;

        if (synchronizationFolderIds.Count > 0)
        {
            // Gather FolderIds to synchronize.

            options.Type = MailSynchronizationType.CustomFolders;
            options.SynchronizationFolderIds = synchronizationFolderIds;
        }
        else
        {
            // At this point it's a mix of everything. Do full sync.
            options.Type = MailSynchronizationType.FullFolders;
        }

        return options;
    }

    /// <summary>
    /// Checks if the mail synchronization should be queued or not.
    /// </summary>
    /// <param name="options">New mail sync request.</param>
    /// <returns>Whether sync should be queued or not.</returns>
    private bool ShouldQueueMailSynchronization(MailSynchronizationOptions options)
    {
        // Multiple IMAPIdle requests are ignored.
        if (options.Type == MailSynchronizationType.IMAPIdle &&
            PendingSynchronizationRequest.Any(a => a.Key.Type == MailSynchronizationType.IMAPIdle))
        {
            return false;
        }

        // Executing requests may trigger idle sync.
        // If there are pending execute requests cancel idle change.

        // TODO: Ideally this check should only work for Inbox execute requests.
        // Check if request folders contains Inbox.

        if (options.Type == MailSynchronizationType.IMAPIdle &&
            PendingSynchronizationRequest.Any(a => a.Key.Type == MailSynchronizationType.ExecuteRequests))
        {
            return false;
        }

        return true;
    }

    #region Mail/Folder Operations

    public virtual bool DelaySendOperationSynchronization() => false;
    public virtual List<IExecutableRequest> Move(BatchMoveRequest request) => throw new NotSupportedException(string.Format(Translator.Exception_UnsupportedSynchronizerOperation, this.GetType()));
    public virtual List<IExecutableRequest> ChangeFlag(BatchChangeFlagRequest request) => throw new NotSupportedException(string.Format(Translator.Exception_UnsupportedSynchronizerOperation, this.GetType()));
    public virtual List<IExecutableRequest> MarkRead(BatchMarkReadRequest request) => throw new NotSupportedException(string.Format(Translator.Exception_UnsupportedSynchronizerOperation, this.GetType()));
    public virtual List<IExecutableRequest> Delete(BatchDeleteRequest request) => throw new NotSupportedException(string.Format(Translator.Exception_UnsupportedSynchronizerOperation, this.GetType()));
    public virtual List<IExecutableRequest> AlwaysMoveTo(BatchAlwaysMoveToRequest request) => throw new NotSupportedException(string.Format(Translator.Exception_UnsupportedSynchronizerOperation, this.GetType()));
    public virtual List<IExecutableRequest> MoveToFocused(BatchMoveToFocusedRequest request) => throw new NotSupportedException(string.Format(Translator.Exception_UnsupportedSynchronizerOperation, this.GetType()));
    public virtual List<IExecutableRequest> CreateDraft(CreateDraftRequest request) => throw new NotSupportedException(string.Format(Translator.Exception_UnsupportedSynchronizerOperation, this.GetType()));
    public virtual List<IExecutableRequest> SendDraft(SendDraftRequest request) => throw new NotSupportedException(string.Format(Translator.Exception_UnsupportedSynchronizerOperation, this.GetType()));
    public virtual List<IExecutableRequest> Archive(BatchArchiveRequest request) => throw new NotSupportedException(string.Format(Translator.Exception_UnsupportedSynchronizerOperation, this.GetType()));
    public virtual List<IExecutableRequest> RenameFolder(RenameFolderRequest request) => throw new NotSupportedException(string.Format(Translator.Exception_UnsupportedSynchronizerOperation, this.GetType()));
    public virtual List<IExecutableRequest> EmptyFolder(EmptyFolderRequest request) => throw new NotSupportedException(string.Format(Translator.Exception_UnsupportedSynchronizerOperation, this.GetType()));
    public virtual List<IExecutableRequest> MarkFolderAsRead(MarkFolderAsReadRequest request) => throw new NotSupportedException(string.Format(Translator.Exception_UnsupportedSynchronizerOperation, this.GetType()));

    #endregion

    #region Calendar Operations

    public virtual List<IExecutableRequest> CreateCalendarEvent(CreateCalendarEventRequest request) => throw new NotSupportedException(string.Format(Translator.Exception_UnsupportedSynchronizerOperation, this.GetType()));
    public virtual List<IExecutableRequest> UpdateCalendarEvent(UpdateCalendarEventRequest request) => throw new NotSupportedException(string.Format(Translator.Exception_UnsupportedSynchronizerOperation, this.GetType()));
    public virtual List<IExecutableRequest> DeleteCalendarEvent(DeleteCalendarEventRequest request) => throw new NotSupportedException(string.Format(Translator.Exception_UnsupportedSynchronizerOperation, this.GetType()));
    public virtual List<IExecutableRequest> AcceptEvent(AcceptEventRequest request) => throw new NotSupportedException(string.Format(Translator.Exception_UnsupportedSynchronizerOperation, this.GetType()));
    public virtual List<IExecutableRequest> DeclineEvent(DeclineEventRequest request) => throw new NotSupportedException(string.Format(Translator.Exception_UnsupportedSynchronizerOperation, this.GetType()));
    public virtual List<IExecutableRequest> OutlookDeclineEvent(OutlookDeclineEventRequest request) => throw new NotSupportedException(string.Format(Translator.Exception_UnsupportedSynchronizerOperation, this.GetType()));
    public virtual List<IExecutableRequest> TentativeEvent(TentativeEventRequest request) => throw new NotSupportedException(string.Format(Translator.Exception_UnsupportedSynchronizerOperation, this.GetType()));

    #endregion


    /// <summary>
    /// Downloads a single missing message from synchronizer and saves it to given FileId from IMailItem.
    /// </summary>
    /// <param name="mailItem">Mail item that its mime file does not exist on the disk.</param>
    /// <param name="transferProgress">Optional download progress for IMAP synchronizer.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public virtual Task DownloadMissingMimeMessageAsync(MailCopy mailItem, ITransferProgress transferProgress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException(string.Format(Translator.Exception_UnsupportedSynchronizerOperation, this.GetType()));

    /// <summary>
    /// Downloads a calendar attachment from the provider.
    /// </summary>
    /// <param name="calendarItem">Calendar item the attachment belongs to.</param>
    /// <param name="attachment">Attachment metadata to download.</param>
    /// <param name="localFilePath">Local file path to save the attachment to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public virtual Task DownloadCalendarAttachmentAsync(
        Wino.Core.Domain.Entities.Calendar.CalendarItem calendarItem,
        Wino.Core.Domain.Entities.Calendar.CalendarAttachment attachment,
        string localFilePath,
        CancellationToken cancellationToken = default) => throw new NotSupportedException(string.Format(Translator.Exception_UnsupportedSynchronizerOperation, this.GetType()));

    /// <summary>
    /// Performs an online search for the given query text in the given folders.
    /// Downloads the missing messages from the server.
    /// </summary>
    /// <param name="queryText">Query to search for.</param>
    /// <param name="folders">Which folders to include in.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="NotSupportedException"></exception>
    public virtual Task<List<MailCopy>> OnlineSearchAsync(string queryText, List<IMailItemFolder> folders, CancellationToken cancellationToken = default) => throw new NotSupportedException(string.Format(Translator.Exception_UnsupportedSynchronizerOperation, this.GetType()));

    public List<IExecutableRequest> CreateSingleTaskBundle(Func<IImapClient, IRequestBase, Task> action, IRequestBase request, IUIChangeRequest uIChangeRequest)
    {
        return [new ImapRequestBundle(new ImapRequest(action, request), request, uIChangeRequest)];
    }

    public List<IExecutableRequest> CreateTaskBundle<TSingeRequestType>(Func<IImapClient, TSingeRequestType, Task> value,
        List<TSingeRequestType> requests)
        where TSingeRequestType : IRequestBase, IUIChangeRequest
    {
        List<IExecutableRequest> ret = [];

        foreach (var request in requests)
        {
            ret.Add(new ImapRequestBundle(new ImapRequest<TSingeRequestType>(value, request), request, request));
        }

        return ret;
    }

    public virtual Task KillSynchronizerAsync()
    {
        IsDisposing = true;
        CancelAllSynchronizations();

        return Task.CompletedTask;
    }

    protected void CancelAllSynchronizations()
    {
        foreach (var request in PendingSynchronizationRequest)
        {
            request.Value.Cancel();
            request.Value.Dispose();
        }
    }
}
