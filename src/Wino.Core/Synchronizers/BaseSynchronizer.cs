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
using Wino.Core.Helpers;
using Wino.Core.Requests.Mail;
using Wino.Core.Requests.Bundles;
using Wino.Messaging.UI;

namespace Wino.Core.Synchronizers;

public abstract partial class BaseSynchronizer<TBaseRequest> : ObservableObject, IBaseSynchronizer
{
    /// <summary>
    /// Serializes mail synchronization for the account.
    /// </summary>
    protected SemaphoreSlim synchronizationSemaphore = new(1);

    /// <summary>
    /// Serializes contact synchronization for the account. Contacts do not share the mail gate:
    /// an initial mail download can take minutes, and contacts must not wait for it.
    /// Calendar already runs under its own per-account lock in the synchronization manager.
    /// </summary>
    protected readonly SemaphoreSlim contactSynchronizationSemaphore = new(1, 1);

    /// <summary>
    /// Serializes task synchronization for the account, independently from mail and contacts.
    /// </summary>
    protected readonly SemaphoreSlim taskSynchronizationSemaphore = new(1, 1);

    protected CancellationToken activeSynchronizationCancellationToken;

    private readonly List<IRequestBase> changeRequestQueue = [];
    private readonly object requestQueueLock = new();
    private readonly ConcurrentDictionary<Guid, byte> _pendingMailOperationIds = new();
    private readonly ConcurrentDictionary<Guid, byte> _pendingCalendarOperationIds = new();
    private readonly ConcurrentDictionary<Guid, byte> _pendingContactOperationIds = new();

    // Modes of one account synchronize concurrently, so each top-level synchronization call keeps
    // its own issue list in its async flow. The shared queue only catches issues captured outside
    // any synchronization call.
    private readonly ConcurrentQueue<SynchronizationIssue> _sharedCapturedSynchronizationIssues = new();
    private readonly AsyncLocal<ConcurrentQueue<SynchronizationIssue>> _flowCapturedSynchronizationIssues = new();

    private ConcurrentQueue<SynchronizationIssue> CapturedSynchronizationIssues
        => _flowCapturedSynchronizationIssues.Value ?? _sharedCapturedSynchronizationIssues;
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
        lock (requestQueueLock)
        {
            TrackQueuedRequest(request);
            changeRequestQueue.Add(request);
        }
    }

    public bool HasQueuedRequests()
    {
        lock (requestQueueLock)
            return changeRequestQueue.Count > 0;
    }

    protected List<IRequestBase> GetQueuedRequests()
    {
        lock (requestQueueLock)
            return changeRequestQueue.ToList();
    }

    protected void RemoveQueuedRequests(IEnumerable<IRequestBase> requests)
    {
        lock (requestQueueLock)
        {
            foreach (var request in requests)
            {
                var index = changeRequestQueue.FindIndex(queued => ReferenceEquals(queued, request));
                if (index >= 0)
                    changeRequestQueue.RemoveAt(index);
            }
        }
    }

    public bool HasPendingOperation(Guid mailUniqueId) => _pendingMailOperationIds.ContainsKey(mailUniqueId);

    public IReadOnlyCollection<Guid> GetPendingOperationUniqueIds() => _pendingMailOperationIds.Keys.ToArray();

    public bool HasPendingCalendarOperation(Guid calendarItemId) => _pendingCalendarOperationIds.ContainsKey(calendarItemId);

    public IReadOnlyCollection<Guid> GetPendingCalendarOperationIds() => _pendingCalendarOperationIds.Keys.ToArray();

    public bool HasPendingContactOperation(Guid contactId) => _pendingContactOperationIds.ContainsKey(contactId);

    public IReadOnlyCollection<Guid> GetPendingContactOperationIds() => _pendingContactOperationIds.Keys.ToArray();

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

        if (request is IContactActionRequest contactActionRequest)
            _pendingContactOperationIds.TryAdd(contactActionRequest.LocalContactId, 0);
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


        if (request is IContactActionRequest contactActionRequest)
            _pendingContactOperationIds.TryRemove(contactActionRequest.LocalContactId, out _);
    }

    protected void UntrackProcessedRequests(IEnumerable<IRequestBase> requests)
    {
        lock (requestQueueLock)
        {
            foreach (var request in requests)
                UntrackProcessedRequest(request);
            // A newer action for the same message, or an unstarted batch, is still pending.
            foreach (var queued in changeRequestQueue)
                TrackQueuedRequest(queued);
        }
    }

    /// <summary>
    /// Starts a fresh issue list for the calling synchronization flow. This is a synchronous
    /// method on purpose: the AsyncLocal value it sets stays visible to the rest of the calling
    /// async method and everything it awaits, but never leaks into a concurrent synchronization
    /// of another mode on the same account.
    /// </summary>
    protected void ResetCapturedSynchronizationIssues()
    {
        _flowCapturedSynchronizationIssues.Value = new ConcurrentQueue<SynchronizationIssue>();

        while (_sharedCapturedSynchronizationIssues.TryDequeue(out _))
        {
        }
    }

    protected void CaptureSynchronizationIssue(SynchronizationIssue issue)
    {
        if (issue == null || string.IsNullOrWhiteSpace(issue.Message))
            return;

        CapturedSynchronizationIssues.Enqueue(issue);
    }

    protected void CaptureSynchronizationIssue(SynchronizerErrorContext errorContext)
        => CaptureSynchronizationIssue(SynchronizationIssue.FromErrorContext(errorContext));

    protected IReadOnlyList<SynchronizationIssue> GetCapturedSynchronizationIssues()
        => CapturedSynchronizationIssues.ToArray();

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
            if (!string.IsNullOrEmpty(profileInformation.AccountAddress))
            {
                Account.Address = profileInformation.AccountAddress;
            }
        }

        return profileInformation;
    }

    /// <summary>
    /// Returns profile picture bytes from the given URL.
    /// </summary>
    /// <param name="url">URL to retrieve picture from.</param>
    protected async Task<byte[]> GetProfilePictureAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
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
        => RequestUiChangeCoordinator.ApplyBundles(bundles, shouldApply);
}
