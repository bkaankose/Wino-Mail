using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
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
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Requests.Bundles;
using Wino.Core.Requests.Folder;
using Wino.Core.Requests.Mail;
using Wino.Messaging.UI;

namespace Wino.Core.Synchronizers
{
    public abstract class BaseMailSynchronizer<TBaseRequest, TMessageType> : IBaseMailSynchronizer
    {
        private SemaphoreSlim synchronizationSemaphore = new(1);
        private CancellationToken activeSynchronizationCancellationToken;

        protected List<IRequestBase> changeRequestQueue = [];
        protected ILogger Logger = Log.ForContext<BaseMailSynchronizer<TBaseRequest, TMessageType>>();

        /// <summary>
        /// How many items per single HTTP call can be modified.
        /// </summary>
        public abstract uint BatchModificationSize { get; }

        /// <summary>
        /// How many items must be downloaded per folder when the folder is first synchronized.
        /// </summary>
        public abstract uint InitialMessageDownloadCountPerFolder { get; }

        protected BaseMailSynchronizer(MailAccount account)
        {
            Account = account;
        }

        public MailAccount Account { get; }

        private AccountSynchronizerState state;
        public AccountSynchronizerState State
        {
            get { return state; }
            private set
            {
                state = value;

                WeakReferenceMessenger.Default.Send(new AccountSynchronizerStateChanged(Account.Id, value));
            }
        }

        /// <summary>
        /// Queues a single request to be executed in the next synchronization.
        /// </summary>
        /// <param name="request">Request to execute.</param>
        public void QueueRequest(IRequestBase request) => changeRequestQueue.Add(request);

        /// <summary>
        /// Creates a new Wino Mail Item package out of native message type with full Mime.
        /// </summary>
        /// <param name="message">Native message type for the synchronizer.</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Package that encapsulates downloaded Mime and additional information for adding new mail.</returns>
        public abstract Task<List<NewMailItemPackage>> CreateNewMailPackagesAsync(TMessageType message, MailItemFolder assignedFolder, CancellationToken cancellationToken = default);

        /// <summary>
        /// Runs existing queued requests in the queue.
        /// </summary>
        /// <param name="batchedRequests">Batched requests to execute. Integrator methods will only receive batched requests.</param>
        /// <param name="cancellationToken">Cancellation token</param>
        public abstract Task ExecuteNativeRequestsAsync(List<IRequestBundle<TBaseRequest>> batchedRequests, CancellationToken cancellationToken = default);

        /// <summary>
        /// Refreshes remote mail account profile if possible.
        /// Profile picture, sender name and mailbox settings (todo) will be handled in this step.
        /// </summary>
        public virtual Task<ProfileInformation> GetProfileInformationAsync() => default;

        /// <summary>
        /// Refreshes the aliases of the account.
        /// Only available for Gmail right now.
        /// </summary>
        protected virtual Task SynchronizeAliasesAsync() => Task.CompletedTask;

        /// <summary>
        /// Returns the base64 encoded profile picture of the account from the given URL.
        /// </summary>
        /// <param name="url">URL to retrieve picture from.</param>
        /// <returns>base64 encoded profile picture</returns>
        protected async Task<string> GetProfilePictureBase64EncodedAsync(string url)
        {
            using var client = new HttpClient();

            var response = await client.GetAsync(url).ConfigureAwait(false);
            var byteContent = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);

            return Convert.ToBase64String(byteContent);
        }

        /// <summary>
        /// Internally synchronizes the account with the given options.
        /// Not exposed and overriden for each synchronizer.
        /// </summary>
        /// <param name="options">Synchronization options.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Synchronization result that contains summary of the sync.</returns>
        protected abstract Task<SynchronizationResult> SynchronizeInternalAsync(SynchronizationOptions options, CancellationToken cancellationToken = default);

        /// <summary>
        /// Safely updates account's profile information.
        /// Database changes are reflected after this call.
        /// </summary>
        private async Task<ProfileInformation> SynchronizeProfileInformationInternalAsync()
        {
            var profileInformation = await GetProfileInformationAsync();

            if (profileInformation != null)
            {
                Account.SenderName = profileInformation.SenderName;
                Account.Base64ProfilePictureData = profileInformation.Base64ProfilePictureData;

                if (!string.IsNullOrEmpty(profileInformation.AccountAddress))
                {
                    Account.Address = profileInformation.AccountAddress;
                }
            }

            return profileInformation;
        }

        /// <summary>
        /// Batches network requests, executes them, and does the needed synchronization after the batch request execution.
        /// </summary>
        /// <param name="options">Synchronization options.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Synchronization result that contains summary of the sync.</returns>
        public async Task<SynchronizationResult> SynchronizeAsync(SynchronizationOptions options, CancellationToken cancellationToken = default)
        {
            try
            {
                activeSynchronizationCancellationToken = cancellationToken;

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

                PublishSynchronizationProgress(1);

                await ExecuteNativeRequestsAsync(nativeRequests, activeSynchronizationCancellationToken);

                PublishUnreadItemChanges();

                // Execute request sync options should be re-calculated after execution.
                // This is the part we decide which individual folders must be synchronized
                // after the batch request execution.
                if (options.Type == SynchronizationType.ExecuteRequests)
                    options = GetSynchronizationOptionsAfterRequestExecution(requestCopies);

                State = AccountSynchronizerState.Synchronizing;

                await synchronizationSemaphore.WaitAsync(activeSynchronizationCancellationToken);

                // Handle special synchronization types.

                // Profile information sync.
                if (options.Type == SynchronizationType.UpdateProfile)
                {
                    if (!Account.IsProfileInfoSyncSupported) return SynchronizationResult.Empty;

                    ProfileInformation newProfileInformation = null;

                    try
                    {
                        newProfileInformation = await SynchronizeProfileInformationInternalAsync();
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Failed to update profile information for {Name}", Account.Name);

                        return SynchronizationResult.Failed;
                    }

                    return SynchronizationResult.Completed(null, newProfileInformation);
                }

                // Alias sync.
                if (options.Type == SynchronizationType.Alias)
                {
                    if (!Account.IsAliasSyncSupported) return SynchronizationResult.Empty;

                    try
                    {
                        await SynchronizeAliasesAsync();

                        return SynchronizationResult.Empty;
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Failed to update aliases for {Name}", Account.Name);

                        return SynchronizationResult.Failed;
                    }
                }

                // Let servers to finish their job. Sometimes the servers doesn't respond immediately.
                // Bug: if Outlook can't create the message in Sent Items folder before this delay,
                // message will not appear in user's inbox since it's not in the Sent Items folder.

                bool shouldDelayExecution =
                    (Account.ProviderType == MailProviderType.Outlook || Account.ProviderType == MailProviderType.Office365)
                    && requestCopies.Any(a => a.ResynchronizationDelay > 0);

                if (shouldDelayExecution)
                {
                    var maxDelay = requestCopies.Aggregate(0, (max, next) => Math.Max(max, next.ResynchronizationDelay));

                    await Task.Delay(maxDelay);
                }

                // Start the internal synchronization.
                var synchronizationResult = await SynchronizeInternalAsync(options, activeSynchronizationCancellationToken).ConfigureAwait(false);

                PublishUnreadItemChanges();

                return synchronizationResult;
            }
            catch (OperationCanceledException)
            {
                Logger.Warning("Synchronization canceled.");

                return SynchronizationResult.Canceled;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Synchronization failed for {Name}", Account.Name);

                throw;
            }
            finally
            {
                // Reset account progress to hide the progress.
                PublishSynchronizationProgress(0);

                State = AccountSynchronizerState.Idle;
                synchronizationSemaphore.Release();
            }
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
        /// 1. Group all requests by operation type.
        /// 2. Group all individual operation type requests with equality check.
        /// Equality comparison in the records are done with RequestComparer
        /// to ignore Item property. Each request can have their own logic for comparison.
        /// For example, move requests for different mails from the same folder to the same folder
        /// must be dispatched in the same batch. This is much faster for the server. Specially IMAP
        /// since all folders must be asynchronously opened/closed.
        /// </summary>
        /// <returns>Batch request collection for all these single requests.</returns>
        //private List<IRequestBase> CreateBatchRequests()
        //{
        //    var batchList = new List<IRequestBase>();
        //    var comparer = new RequestComparer();

        //    while (changeRequestQueue.Count > 0)
        //    {
        //        if (changeRequestQueue.TryPeek(out IRequestBase request))
        //        {
        //            // Mail request, must be batched.
        //            if (request is IMailActionRequest mailRequest)
        //            {
        //                var equalItems = changeRequestQueue
        //                    .Where(a => a is IMailActionRequest && comparer.Equals(a, request))
        //                    .Cast<IMailActionRequest>()
        //                    .ToList();

        //                batchList.Add(mailRequest.CreateBatch(equalItems));

        //                // Remove these items from the queue.
        //                foreach (var item in equalItems)
        //                {
        //                    changeRequestQueue.TryTake(out _);
        //                }
        //            }
        //            else if (changeRequestQueue.TryTake(out request))
        //            {
        //                // This is a folder operation.
        //                // There is no need to batch them since Users can't do folder ops in bulk.

        //                batchList.Add(request);
        //            }
        //        }
        //    }

        //    return batchList;
        //}

        /// <summary>
        /// Converts batched requests into HTTP/Task calls that derived synchronizers can execute.
        /// </summary>
        /// <param name="batchChangeRequests">Batch requests to be converted.</param>
        /// <returns>Collection of native requests for individual synchronizer type.</returns>
        //private IEnumerable<IRequestBundle<TBaseRequest>> CreateNativeRequestBundles(IEnumerable<IRequestBase> batchChangeRequests)
        //{
        //    IEnumerable<IEnumerable<IRequestBundle<TBaseRequest>>> GetNativeRequests()
        //    {
        //        foreach (var item in batchChangeRequests)
        //        {
        //            switch (item.Operation)
        //            {
        //                case MailSynchronizerOperation.Send:
        //                    yield return SendDraft((BatchSendDraftRequestRequest)item);
        //                    break;
        //                case MailSynchronizerOperation.MarkRead:
        //                    yield return MarkRead((BatchMarkReadRequest)item);
        //                    break;
        //                case MailSynchronizerOperation.Move:
        //                    yield return Move((BatchMoveRequest)item);
        //                    break;
        //                case MailSynchronizerOperation.Delete:
        //                    yield return Delete((BatchDeleteRequest)item);
        //                    break;
        //                case MailSynchronizerOperation.ChangeFlag:
        //                    yield return ChangeFlag((BatchChangeFlagRequest)item);
        //                    break;
        //                case MailSynchronizerOperation.AlwaysMoveTo:
        //                    yield return AlwaysMoveTo((BatchAlwaysMoveToRequest)item);
        //                    break;
        //                case MailSynchronizerOperation.MoveToFocused:
        //                    yield return MoveToFocused((BatchMoveToFocusedRequest)item);
        //                    break;
        //                case MailSynchronizerOperation.CreateDraft:
        //                    yield return CreateDraft((BatchCreateDraftRequest)item);
        //                    break;
        //                case MailSynchronizerOperation.RenameFolder:
        //                    yield return RenameFolder((RenameFolderRequest)item);
        //                    break;
        //                case MailSynchronizerOperation.EmptyFolder:
        //                    yield return EmptyFolder((EmptyFolderRequest)item);
        //                    break;
        //                case MailSynchronizerOperation.MarkFolderRead:
        //                    yield return MarkFolderAsRead((MarkFolderAsReadRequest)item);
        //                    break;
        //                case MailSynchronizerOperation.Archive:
        //                    yield return Archive((BatchArchiveRequest)item);
        //                    break;
        //            }
        //        }
        //    };

        //    return GetNativeRequests().SelectMany(collections => collections);
        //}

        /// <summary>
        /// Attempts to find out the best possible synchronization options after the batch request execution.
        /// </summary>
        /// <param name="batches">Batch requests to run in synchronization.</param>
        /// <returns>New synchronization options with minimal HTTP effort.</returns>
        private SynchronizationOptions GetSynchronizationOptionsAfterRequestExecution(List<IRequestBase> requests)
        {
            List<Guid> synchronizationFolderIds = requests
                    .Where(a => a is ICustomFolderSynchronizationRequest)
                    .Cast<ICustomFolderSynchronizationRequest>()
                    .SelectMany(a => a.SynchronizationFolderIds)
                    .ToList();

            var options = new SynchronizationOptions()
            {
                AccountId = Account.Id,
            };

            if (synchronizationFolderIds.Count > 0)
            {
                // Gather FolderIds to synchronize.

                options.Type = SynchronizationType.CustomFolders;
                options.SynchronizationFolderIds = synchronizationFolderIds;
            }
            else
            {
                // At this point it's a mix of everything. Do full sync.
                options.Type = SynchronizationType.FullFolders;
            }

            return options;
        }

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


        /// <summary>
        /// Downloads a single missing message from synchronizer and saves it to given FileId from IMailItem.
        /// </summary>
        /// <param name="mailItem">Mail item that its mime file does not exist on the disk.</param>
        /// <param name="transferProgress">Optional download progress for IMAP synchronizer.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        public virtual Task DownloadMissingMimeMessageAsync(IMailItem mailItem, ITransferProgress transferProgress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException(string.Format(Translator.Exception_UnsupportedSynchronizerOperation, this.GetType()));

        public bool CancelActiveSynchronization()
        {
            // TODO: What if account is deleted during synchronization?
            return true;
        }

        #region Bundle Helpers

        public List<IRequestBundle<TBaseRequest>> ForEachRequest<TWinoRequestType>(IEnumerable<TWinoRequestType> requests,
            Func<TWinoRequestType, TBaseRequest> action)
            where TWinoRequestType : IRequestBase
        {
            List<IRequestBundle<TBaseRequest>> ret = [];

            foreach (var request in requests)
                ret.Add(new HttpRequestBundle<TBaseRequest>(action(request), request));

            return ret;
        }

        public List<IRequestBundle<ImapRequest>> CreateSingleBundle(Func<ImapClient, IRequestBase, Task> action, IRequestBase request, IUIChangeRequest uIChangeRequest)
        {
            return [new ImapRequestBundle(new ImapRequest(action, request), request, uIChangeRequest)];
        }

        public List<IRequestBundle<ImapRequest>> CreateTaskBundle<TSingeRequestType>(Func<ImapClient, TSingeRequestType, Task> value,
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

        #endregion
    }
}
