using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Requests.Mail;
using Wino.Core.Requests.Bundles;
using Wino.Messaging.UI;

namespace Wino.Core.Synchronizers;

public abstract partial class BaseSynchronizer<TBaseRequest> : ObservableObject, IBaseSynchronizer
{
    protected SemaphoreSlim synchronizationSemaphore = new(1);
    protected CancellationToken activeSynchronizationCancellationToken;

    protected List<IRequestBase> changeRequestQueue = [];
    private readonly ConcurrentDictionary<Guid, byte> _pendingMailOperationIds = new();
    private readonly ConcurrentDictionary<Guid, byte> _pendingCalendarOperationIds = new();
    private readonly ConcurrentQueue<SynchronizationIssue> _capturedSynchronizationIssues = new();
    protected readonly IMessenger Messenger;
    protected SynchronizationProgressCategory CurrentSynchronizationProgressCategory { get; set; } = SynchronizationProgressCategory.Mail;
    
    public MailAccount Account { get; }

    private AccountSynchronizerState state;
    public AccountSynchronizerState State
    {
        get { return state; }
        set
        {
            state = value;

            // Send state changed message with current progress information
            Messenger.Send(new AccountSynchronizerStateChanged(
                Account.Id, 
                value, 
                TotalItemsToSync, 
                RemainingItemsToSync, 
                SynchronizationStatus,
                CurrentSynchronizationProgressCategory));
        }
    }

    /// <summary>
    /// Current synchronization status message.
    /// </summary>
    [ObservableProperty]
    public partial string SynchronizationStatus { get; set; } = string.Empty;

    /// <summary>
    /// Total items to download/sync in current operation. 
    /// 0 means no active download or indeterminate progress.
    /// </summary>
    [ObservableProperty]
    public partial int TotalItemsToSync { get; set; }

    /// <summary>
    /// Remaining items to download/sync in current operation.
    /// </summary>
    [ObservableProperty]
    public partial int RemainingItemsToSync { get; set; }

    /// <summary>
    /// Calculated progress percentage (0-100) based on TotalItemsToSync and RemainingItemsToSync.
    /// Returns -1 for indeterminate progress (when both are 0).
    /// </summary>
    public double SynchronizationProgress
    {
        get
        {
            if (TotalItemsToSync <= 0)
                return 0;

            return ((double)(TotalItemsToSync - RemainingItemsToSync) / TotalItemsToSync) * 100;
        }
    }

    protected BaseSynchronizer(MailAccount account, IMessenger messenger)
    {
        Account = account;
        Messenger = messenger ?? WeakReferenceMessenger.Default;
    }

    /// <summary>
    /// Resets synchronization progress to default state.
    /// </summary>
    protected void ResetSyncProgress()
    {
        TotalItemsToSync = 0;
        RemainingItemsToSync = 0;
        SynchronizationStatus = string.Empty;
        OnPropertyChanged(nameof(SynchronizationProgress));
    }

    /// <summary>
    /// Updates synchronization progress with current item counts.
    /// </summary>
    /// <param name="total">Total items to sync</param>
    /// <param name="remaining">Remaining items to sync</param>
    /// <param name="status">Optional status message</param>
    protected void UpdateSyncProgress(int total, int remaining, string status = "")
    {
        TotalItemsToSync = total;
        RemainingItemsToSync = remaining;
        SynchronizationStatus = status;
        OnPropertyChanged(nameof(SynchronizationProgress));
        
        // Send progress update message
        Messenger.Send(new AccountSynchronizerStateChanged(
            Account.Id, 
            State, 
            TotalItemsToSync, 
            RemainingItemsToSync, 
            SynchronizationStatus,
            CurrentSynchronizationProgressCategory));
    }

    /// <summary>
    /// Queues a single request to be executed in the next synchronization.
    /// </summary>
    /// <param name="request">Request to execute.</param>
    public void QueueRequest(IRequestBase request)
    {
        changeRequestQueue.Add(request);
        TrackQueuedRequest(request);
    }

    public bool HasPendingOperation(Guid mailUniqueId) => _pendingMailOperationIds.ContainsKey(mailUniqueId);

    public IReadOnlyCollection<Guid> GetPendingOperationUniqueIds() => _pendingMailOperationIds.Keys.ToArray();

    public bool HasPendingCalendarOperation(Guid calendarItemId) => _pendingCalendarOperationIds.ContainsKey(calendarItemId);

    public IReadOnlyCollection<Guid> GetPendingCalendarOperationIds() => _pendingCalendarOperationIds.Keys.ToArray();

    protected void TrackQueuedRequest(IRequestBase request)
    {
        if (request is IMailActionRequest mailActionRequest)
        {
            _pendingMailOperationIds.TryAdd(mailActionRequest.Item.UniqueId, 0);
        }

        if (request is ICalendarActionRequest calendarActionRequest)
        {
            if (calendarActionRequest.LocalCalendarItemId.HasValue)
            {
                _pendingCalendarOperationIds.TryAdd(calendarActionRequest.LocalCalendarItemId.Value, 0);
            }
        }
    }

    protected void UntrackProcessedRequest(IRequestBase request)
    {
        if (request is IMailActionRequest mailActionRequest)
        {
            _pendingMailOperationIds.TryRemove(mailActionRequest.Item.UniqueId, out _);
        }

        if (request is ICalendarActionRequest calendarActionRequest)
        {
            if (calendarActionRequest.LocalCalendarItemId.HasValue)
            {
                _pendingCalendarOperationIds.TryRemove(calendarActionRequest.LocalCalendarItemId.Value, out _);
            }
        }
    }

    protected void UntrackProcessedRequests(IEnumerable<IRequestBase> requests)
    {
        foreach (var request in requests)
            UntrackProcessedRequest(request);
    }

    protected void ResetCapturedSynchronizationIssues()
    {
        while (_capturedSynchronizationIssues.TryDequeue(out _))
        {
        }
    }

    protected void CaptureSynchronizationIssue(SynchronizationIssue issue)
    {
        if (issue == null || string.IsNullOrWhiteSpace(issue.Message))
            return;

        _capturedSynchronizationIssues.Enqueue(issue);
    }

    protected void CaptureSynchronizationIssue(SynchronizerErrorContext errorContext)
        => CaptureSynchronizationIssue(SynchronizationIssue.FromErrorContext(errorContext));

    protected IReadOnlyList<SynchronizationIssue> GetCapturedSynchronizationIssues()
        => _capturedSynchronizationIssues.ToArray();

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
    /// Safely updates account's profile information.
    /// Database changes are reflected after this call.
    /// </summary>
    protected async Task<ProfileInformation> SynchronizeProfileInformationInternalAsync()
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

    public List<IRequestBundle<TBaseRequest>> ForEachRequest<TWinoRequestType>(IEnumerable<TWinoRequestType> requests,
                Func<TWinoRequestType, TBaseRequest> action)
                where TWinoRequestType : IRequestBase
    {
        List<IRequestBundle<TBaseRequest>> ret = [];

        foreach (var request in requests)
            ret.Add(new HttpRequestBundle<TBaseRequest>(action(request), request, request));

        return ret;
    }

    protected void ApplyOptimisticUiChanges(IEnumerable<IRequestBundle<TBaseRequest>> bundles, Func<IRequestBase, bool> shouldApply = null)
    {
        var bundleList = bundles?
            .Where(b => b?.Request != null && (shouldApply?.Invoke(b.Request) ?? true))
            .ToList() ?? [];

        if (bundleList.Count == 0)
            return;

        var requestList = new List<IRequestBase>(bundleList.Count);

        foreach (var bundle in bundleList)
        {
            if (bundle.UIChangeRequest != null && !ReferenceEquals(bundle.UIChangeRequest, bundle.Request))
            {
                bundle.UIChangeRequest.ApplyUIChanges();
                continue;
            }

            requestList.Add(bundle.Request);
        }

        if (requestList.Count == 0)
            return;

        var appliedBatchRequestKeys = new HashSet<object>();

        foreach (var group in requestList.GroupBy(r => r.GroupingKey()))
        {
            var groupRequests = group.ToList();
            if (groupRequests.Count <= 1)
                continue;

            if (!TryApplyBatchUiChanges(groupRequests))
                continue;

            appliedBatchRequestKeys.Add(group.Key);
        }

        foreach (var request in requestList)
        {
            if (!appliedBatchRequestKeys.Contains(request.GroupingKey()))
            {
                request.ApplyUIChanges();
            }
        }
    }

    private static bool TryApplyBatchUiChanges(IReadOnlyList<IRequestBase> requests)
    {
        if (requests == null || requests.Count <= 1)
            return false;

        return requests[0] switch
        {
            MarkReadRequest => ApplyBatch(new BatchMarkReadRequest(requests.Cast<MarkReadRequest>())),
            ChangeFlagRequest => ApplyBatch(new BatchChangeFlagRequest(requests.Cast<ChangeFlagRequest>())),
            DeleteRequest => ApplyBatch(new BatchDeleteRequest(requests.Cast<DeleteRequest>())),
            MoveRequest => ApplyBatch(new BatchMoveRequest(requests.Cast<MoveRequest>())),
            ArchiveRequest => ApplyBatch(new BatchArchiveRequest(requests.Cast<ArchiveRequest>())),
            ChangeJunkStateRequest => ApplyBatch(new BatchChangeJunkStateRequest(requests.Cast<ChangeJunkStateRequest>())),
            _ => false
        };

        static bool ApplyBatch(IUIChangeRequest request)
        {
            request.ApplyUIChanges();
            return true;
        }
    }
}
