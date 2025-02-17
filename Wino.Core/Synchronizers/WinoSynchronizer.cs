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
using Wino.Core.Requests.Folder;
using Wino.Core.Requests.Mail;
using Wino.Messaging.UI;

namespace Wino.Core.Synchronizers;

public abstract class WinoSynchronizer<TBaseRequest, TMessageType, TCalendarEventType> : BaseSynchronizer<TBaseRequest>, IWinoSynchronizerBase
{
    protected bool IsDisposing { get; private set; }

    protected Dictionary<MailSynchronizationOptions, CancellationTokenSource> PendingSynchronizationRequest = new();

    protected ILogger Logger = Log.ForContext<WinoSynchronizer<TBaseRequest, TMessageType, TCalendarEventType>>();

    protected WinoSynchronizer(MailAccount account) : base(account) { }

    /// <summary>
    /// How many items per single HTTP call can be modified.
    /// </summary>
    public abstract uint BatchModificationSize { get; }

    /// <summary>
    /// How many items must be downloaded per folder when the folder is first synchronized.
    /// </summary>
    public abstract uint InitialMessageDownloadCountPerFolder { get; }

    /// <summary>
    /// Creates a new Wino Mail Item package out of native message type with full Mime.
    /// </summary>
    /// <param name="message">Native message type for the synchronizer.</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Package that encapsulates downloaded Mime and additional information for adding new mail.</returns>
    public abstract Task<List<NewMailItemPackage>> CreateNewMailPackagesAsync(TMessageType message, MailItemFolder assignedFolder, CancellationToken cancellationToken = default);

    /// <summary>
    /// Refreshes the aliases of the account.
    /// Only available for Gmail right now.
    /// </summary>
    protected virtual Task SynchronizeAliasesAsync() => Task.CompletedTask;

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

            await synchronizationSemaphore.WaitAsync(activeSynchronizationCancellationToken);

            PublishSynchronizationProgress(1);

            // ImapSynchronizer will send this type when an Idle client receives a notification of changes.
            // We should not execute requests in this case.
            bool shouldExecuteRequests = options.Type != MailSynchronizationType.IMAPIdle;

            bool shouldDelayExecution = false;
            int maxExecutionDelay = 0;

            if (shouldExecuteRequests && changeRequestQueue.Any())
            {
                State = AccountSynchronizerState.ExecutingRequests;

                List<IRequestBundle<TBaseRequest>> nativeRequests = new();

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

                    return MailSynchronizationResult.Failed;
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

                    return MailSynchronizationResult.Failed;
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

            // Reset account progress to hide the progress.
            PublishSynchronizationProgress(0);

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
    public Task<CalendarSynchronizationResult> SynchronizeCalendarEventsAsync(CalendarSynchronizationOptions options, CancellationToken cancellationToken = default)
    {
        // TODO: Execute requests for calendar events.
        return SynchronizeCalendarEventsInternalAsync(options, cancellationToken);
    }

    /// <summary>
    /// Updates unread item counts for some folders and account.
    /// Sends a message that shell can pick up and update the UI.
    /// </summary>
    private void PublishUnreadItemChanges()
        => WeakReferenceMessenger.Default.Send(new RefreshUnreadCountsMessage(Account.Id));

    /// <summary>
    /// Sends a message to the shell to update the synchronization progress.
    /// </summary>
    /// <param name="progress">Percentage of the progress.</param>
    public void PublishSynchronizationProgress(double progress)
        => WeakReferenceMessenger.Default.Send(new AccountSynchronizationProgressUpdatedMessage(Account.Id, progress));

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
    public virtual List<IRequestBundle<TBaseRequest>> Move(BatchMoveRequest request) => throw new NotSupportedException(string.Format(Translator.Exception_UnsupportedSynchronizerOperation, this.GetType()));
    public virtual List<IRequestBundle<TBaseRequest>> ChangeFlag(BatchChangeFlagRequest request) => throw new NotSupportedException(string.Format(Translator.Exception_UnsupportedSynchronizerOperation, this.GetType()));
    public virtual List<IRequestBundle<TBaseRequest>> MarkRead(BatchMarkReadRequest request) => throw new NotSupportedException(string.Format(Translator.Exception_UnsupportedSynchronizerOperation, this.GetType()));
    public virtual List<IRequestBundle<TBaseRequest>> Delete(BatchDeleteRequest request) => throw new NotSupportedException(string.Format(Translator.Exception_UnsupportedSynchronizerOperation, this.GetType()));
    public virtual List<IRequestBundle<TBaseRequest>> AlwaysMoveTo(BatchAlwaysMoveToRequest request) => throw new NotSupportedException(string.Format(Translator.Exception_UnsupportedSynchronizerOperation, this.GetType()));
    public virtual List<IRequestBundle<TBaseRequest>> MoveToFocused(BatchMoveToFocusedRequest request) => throw new NotSupportedException(string.Format(Translator.Exception_UnsupportedSynchronizerOperation, this.GetType()));
    public virtual List<IRequestBundle<TBaseRequest>> CreateDraft(CreateDraftRequest request) => throw new NotSupportedException(string.Format(Translator.Exception_UnsupportedSynchronizerOperation, this.GetType()));
    public virtual List<IRequestBundle<TBaseRequest>> SendDraft(SendDraftRequest request) => throw new NotSupportedException(string.Format(Translator.Exception_UnsupportedSynchronizerOperation, this.GetType()));
    public virtual List<IRequestBundle<TBaseRequest>> Archive(BatchArchiveRequest request) => throw new NotSupportedException(string.Format(Translator.Exception_UnsupportedSynchronizerOperation, this.GetType()));
    public virtual List<IRequestBundle<TBaseRequest>> RenameFolder(RenameFolderRequest request) => throw new NotSupportedException(string.Format(Translator.Exception_UnsupportedSynchronizerOperation, this.GetType()));
    public virtual List<IRequestBundle<TBaseRequest>> EmptyFolder(EmptyFolderRequest request) => throw new NotSupportedException(string.Format(Translator.Exception_UnsupportedSynchronizerOperation, this.GetType()));
    public virtual List<IRequestBundle<TBaseRequest>> MarkFolderAsRead(MarkFolderAsReadRequest request) => throw new NotSupportedException(string.Format(Translator.Exception_UnsupportedSynchronizerOperation, this.GetType()));

    #endregion

    #region Calendar Operations


    #endregion


    /// <summary>
    /// Downloads a single missing message from synchronizer and saves it to given FileId from IMailItem.
    /// </summary>
    /// <param name="mailItem">Mail item that its mime file does not exist on the disk.</param>
    /// <param name="transferProgress">Optional download progress for IMAP synchronizer.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public virtual Task DownloadMissingMimeMessageAsync(IMailItem mailItem, ITransferProgress transferProgress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException(string.Format(Translator.Exception_UnsupportedSynchronizerOperation, this.GetType()));

    /// <summary>
    /// Performs an online search for the given query text in the given folders.
    /// Downloads the missing messages from the server.
    /// </summary>
    /// <param name="queryText">Query to search for.</param>
    /// <param name="folders">Which folders to include in.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="NotSupportedException"></exception>
    public virtual Task<List<MailCopy>> OnlineSearchAsync(string queryText, List<IMailItemFolder> folders, CancellationToken cancellationToken = default) => throw new NotSupportedException(string.Format(Translator.Exception_UnsupportedSynchronizerOperation, this.GetType()));

    public List<IRequestBundle<ImapRequest>> CreateSingleTaskBundle(Func<IImapClient, IRequestBase, Task> action, IRequestBase request, IUIChangeRequest uIChangeRequest)
    {
        return [new ImapRequestBundle(new ImapRequest(action, request), request, uIChangeRequest)];
    }

    public List<IRequestBundle<ImapRequest>> CreateTaskBundle<TSingeRequestType>(Func<IImapClient, TSingeRequestType, Task> value,
        List<TSingeRequestType> requests)
        where TSingeRequestType : IRequestBase, IUIChangeRequest
    {
        List<IRequestBundle<ImapRequest>> ret = [];

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
