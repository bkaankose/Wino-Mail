using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using MailKit;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Integration;
using Wino.Core.Messages.Mails;
using Wino.Core.Messages.Synchronization;
using Wino.Core.Misc;
using Wino.Core.Requests;

namespace Wino.Core.Synchronizers
{
    public interface IBaseSynchronizer
    {
        /// <summary>
        /// Account that is assigned for this synchronizer.
        /// </summary>
        MailAccount Account { get; }

        /// <summary>
        /// Synchronizer state.
        /// </summary>
        AccountSynchronizerState State { get; }

        /// <summary>
        /// Queues a single request to be executed in the next synchronization.
        /// </summary>
        /// <param name="request">Request to queue.</param>
        void QueueRequest(IRequestBase request);

        /// <summary>
        /// TODO
        /// </summary>
        /// <returns>Whether active synchronization is stopped or not.</returns>
        bool CancelActiveSynchronization();

        /// <summary>
        /// Performs a full synchronization with the server with given options.
        /// This will also prepares batch requests for execution.
        /// Requests are executed in the order they are queued and happens before the synchronization.
        /// Result of the execution queue is processed during the synchronization.
        /// </summary>
        /// <param name="options">Options for synchronization.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Result summary of synchronization.</returns>
        Task<SynchronizationResult> SynchronizeAsync(SynchronizationOptions options, CancellationToken cancellationToken = default);

        /// <summary>
        /// Downloads a single MIME message from the server and saves it to disk.
        /// </summary>
        /// <param name="mailItem">Mail item to download from server.</param>
        /// <param name="transferProgress">Optional progress reporting for download operation.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        Task DownloadMissingMimeMessageAsync(IMailItem mailItem, ITransferProgress transferProgress, CancellationToken cancellationToken = default);
    }

    public abstract class BaseSynchronizer<TBaseRequest, TMessageType> : BaseMailIntegrator<TBaseRequest>, IBaseSynchronizer
    {
        private SemaphoreSlim synchronizationSemaphore = new(1);
        private CancellationToken activeSynchronizationCancellationToken;

        protected ConcurrentBag<IRequestBase> changeRequestQueue = [];
        protected ILogger Logger = Log.ForContext<BaseSynchronizer<TBaseRequest, TMessageType>>();

        protected BaseSynchronizer(MailAccount account)
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

                WeakReferenceMessenger.Default.Send(new AccountSynchronizerStateChanged(this, value));
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
        public abstract Task ExecuteNativeRequestsAsync(IEnumerable<IRequestBundle<TBaseRequest>> batchedRequests, CancellationToken cancellationToken = default);

        public abstract Task<SynchronizationResult> SynchronizeInternalAsync(SynchronizationOptions options, CancellationToken cancellationToken = default);

        public async Task<SynchronizationResult> SynchronizeAsync(SynchronizationOptions options, CancellationToken cancellationToken = default)
        {
            try
            {
                activeSynchronizationCancellationToken = cancellationToken;

                var batches = CreateBatchRequests().Distinct();

                if (batches.Any())
                {
                    Logger.Information($"{batches?.Count() ?? 0} batched requests");

                    State = AccountSynchronizerState.ExecutingRequests;

                    var nativeRequests = CreateNativeRequestBundles(batches);

                    Console.WriteLine($"Prepared {nativeRequests.Count()} native requests");

                    await ExecuteNativeRequestsAsync(nativeRequests, activeSynchronizationCancellationToken);

                    PublishUnreadItemChanges();

                    // Execute request sync options should be re-calculated after execution.
                    // This is the part we decide which individual folders must be synchronized
                    // after the batch request execution.
                    if (options.Type == SynchronizationType.ExecuteRequests)
                        options = GetSynchronizationOptionsAfterRequestExecution(batches);
                }

                State = AccountSynchronizerState.Synchronizing;

                await synchronizationSemaphore.WaitAsync(activeSynchronizationCancellationToken);

                // Let servers to finish their job. Sometimes the servers doesn't respond immediately.

                bool shouldDelayExecution = batches.Any(a => a.DelayExecution);

                if (shouldDelayExecution)
                {
                    await Task.Delay(2000);
                }

                // Start the internal synchronization.
                var synchronizationResult = await SynchronizeInternalAsync(options, activeSynchronizationCancellationToken).ConfigureAwait(false);

                PublishUnreadItemChanges();

                return synchronizationResult;
            }
            catch (OperationCanceledException)
            {
                Logger.Warning("Synchronization cancelled.");
                return SynchronizationResult.Canceled;
            }
            catch (Exception)
            {
                // Disable maybe?
                throw;
            }
            finally
            {
                // Reset account progress to hide the progress.
                options.ProgressListener?.AccountProgressUpdated(Account.Id, 0);

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
        /// 1. Group all requests by operation type.
        /// 2. Group all individual operation type requests with equality check.
        /// Equality comparison in the records are done with RequestComparer
        /// to ignore Item property. Each request can have their own logic for comparison.
        /// For example, move requests for different mails from the same folder to the same folder
        /// must be dispatched in the same batch. This is much faster for the server. Specially IMAP
        /// since all folders must be asynchronously opened/closed.
        /// </summary>
        /// <returns>Batch request collection for all these single requests.</returns>
        private List<IRequestBase> CreateBatchRequests()
        {
            var batchList = new List<IRequestBase>();
            var comparer = new RequestComparer();

            while (changeRequestQueue.Count > 0)
            {
                if (changeRequestQueue.TryPeek(out IRequestBase request))
                {
                    // Mail request, must be batched.
                    if (request is IRequest mailRequest)
                    {
                        var equalItems = changeRequestQueue
                            .Where(a => a is IRequest && comparer.Equals(a, request))
                            .Cast<IRequest>()
                            .ToList();

                        batchList.Add(mailRequest.CreateBatch(equalItems));

                        // Remove these items from the queue.
                        foreach (var item in equalItems)
                        {
                            changeRequestQueue.TryTake(out _);
                        }
                    }
                    else if (changeRequestQueue.TryTake(out request))
                    {
                        // This is a folder operation.
                        // There is no need to batch them since Users can't do folder ops in bulk.

                        batchList.Add(request);
                    }
                }
            }

            return batchList;
        }

        /// <summary>
        /// Converts batched requests into HTTP/Task calls that derived synchronizers can execute.
        /// </summary>
        /// <param name="batchChangeRequests">Batch requests to be converted.</param>
        /// <returns>Collection of native requests for individual synchronizer type.</returns>
        private IEnumerable<IRequestBundle<TBaseRequest>> CreateNativeRequestBundles(IEnumerable<IRequestBase> batchChangeRequests)
        {
            IEnumerable<IEnumerable<IRequestBundle<TBaseRequest>>> GetNativeRequests()
            {
                foreach (var item in batchChangeRequests)
                {
                    switch (item.Operation)
                    {
                        case MailSynchronizerOperation.Send:
                            yield return SendDraft((BatchSendDraftRequestRequest)item);
                            break;
                        case MailSynchronizerOperation.MarkRead:
                            yield return MarkRead((BatchMarkReadRequest)item);
                            break;
                        case MailSynchronizerOperation.Move:
                            yield return Move((BatchMoveRequest)item);
                            break;
                        case MailSynchronizerOperation.Delete:
                            yield return Delete((BatchDeleteRequest)item);
                            break;
                        case MailSynchronizerOperation.ChangeFlag:
                            yield return ChangeFlag((BatchChangeFlagRequest)item);
                            break;
                        case MailSynchronizerOperation.AlwaysMoveTo:
                            yield return AlwaysMoveTo((BatchAlwaysMoveToRequest)item);
                            break;
                        case MailSynchronizerOperation.MoveToFocused:
                            yield return MoveToFocused((BatchMoveToFocusedRequest)item);
                            break;
                        case MailSynchronizerOperation.CreateDraft:
                            yield return CreateDraft((BatchCreateDraftRequest)item);
                            break;
                        case MailSynchronizerOperation.RenameFolder:
                            yield return RenameFolder((RenameFolderRequest)item);
                            break;
                        case MailSynchronizerOperation.EmptyFolder:
                            yield return EmptyFolder((EmptyFolderRequest)item);
                            break;
                        case MailSynchronizerOperation.MarkFolderRead:
                            yield return MarkFolderAsRead((MarkFolderAsReadRequest)item);
                            break;
                        case MailSynchronizerOperation.Archive:
                            yield return Archive((BatchArchiveRequest)item);
                            break;
                    }
                }
            };

            return GetNativeRequests().SelectMany(collections => collections);
        }

        /// <summary>
        /// Attempts to find out the best possible synchronization options after the batch request execution.
        /// </summary>
        /// <param name="batches">Batch requests to run in synchronization.</param>
        /// <returns>New synchronization options with minimal HTTP effort.</returns>
        private SynchronizationOptions GetSynchronizationOptionsAfterRequestExecution(IEnumerable<IRequestBase> requests)
        {
            bool isAllCustomSynchronizationRequests = requests.All(a => a is ICustomFolderSynchronizationRequest);

            var options = new SynchronizationOptions()
            {
                AccountId = Account.Id,
                Type = SynchronizationType.FoldersOnly
            };

            if (isAllCustomSynchronizationRequests)
            {
                // Gather FolderIds to synchronize.

                options.Type = SynchronizationType.Custom;
                options.SynchronizationFolderIds = requests.Cast<ICustomFolderSynchronizationRequest>().SelectMany(a => a.SynchronizationFolderIds).ToList();
            }
            else
            {
                // At this point it's a mix of everything. Do full sync.
                options.Type = SynchronizationType.Full;
            }

            return options;
        }

        public virtual bool DelaySendOperationSynchronization() => false;
        public virtual IEnumerable<IRequestBundle<TBaseRequest>> Move(BatchMoveRequest request) => throw new NotSupportedException(string.Format(Translator.Exception_UnsupportedSynchronizerOperation, this.GetType()));
        public virtual IEnumerable<IRequestBundle<TBaseRequest>> ChangeFlag(BatchChangeFlagRequest request) => throw new NotSupportedException(string.Format(Translator.Exception_UnsupportedSynchronizerOperation, this.GetType()));
        public virtual IEnumerable<IRequestBundle<TBaseRequest>> MarkRead(BatchMarkReadRequest request) => throw new NotSupportedException(string.Format(Translator.Exception_UnsupportedSynchronizerOperation, this.GetType()));
        public virtual IEnumerable<IRequestBundle<TBaseRequest>> Delete(BatchDeleteRequest request) => throw new NotSupportedException(string.Format(Translator.Exception_UnsupportedSynchronizerOperation, this.GetType()));
        public virtual IEnumerable<IRequestBundle<TBaseRequest>> AlwaysMoveTo(BatchAlwaysMoveToRequest request) => throw new NotSupportedException(string.Format(Translator.Exception_UnsupportedSynchronizerOperation, this.GetType()));
        public virtual IEnumerable<IRequestBundle<TBaseRequest>> MoveToFocused(BatchMoveToFocusedRequest request) => throw new NotSupportedException(string.Format(Translator.Exception_UnsupportedSynchronizerOperation, this.GetType()));
        public virtual IEnumerable<IRequestBundle<TBaseRequest>> CreateDraft(BatchCreateDraftRequest request) => throw new NotSupportedException(string.Format(Translator.Exception_UnsupportedSynchronizerOperation, this.GetType()));
        public virtual IEnumerable<IRequestBundle<TBaseRequest>> SendDraft(BatchSendDraftRequestRequest request) => throw new NotSupportedException(string.Format(Translator.Exception_UnsupportedSynchronizerOperation, this.GetType()));
        public virtual IEnumerable<IRequestBundle<TBaseRequest>> RenameFolder(RenameFolderRequest request) => throw new NotSupportedException(string.Format(Translator.Exception_UnsupportedSynchronizerOperation, this.GetType()));
        public virtual IEnumerable<IRequestBundle<TBaseRequest>> EmptyFolder(EmptyFolderRequest request) => throw new NotSupportedException(string.Format(Translator.Exception_UnsupportedSynchronizerOperation, this.GetType()));
        public virtual IEnumerable<IRequestBundle<TBaseRequest>> MarkFolderAsRead(MarkFolderAsReadRequest request) => throw new NotSupportedException(string.Format(Translator.Exception_UnsupportedSynchronizerOperation, this.GetType()));
        public virtual IEnumerable<IRequestBundle<TBaseRequest>> Archive(BatchArchiveRequest request) => throw new NotSupportedException(string.Format(Translator.Exception_UnsupportedSynchronizerOperation, this.GetType()));

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
    }
}
