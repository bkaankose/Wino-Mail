using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Requests.Bundles;
using Wino.Messaging.UI;

namespace Wino.Core.Synchronizers;

public abstract partial class BaseSynchronizer<TBaseRequest> : ObservableObject, IBaseSynchronizer
    where TBaseRequest : class
{
    protected SemaphoreSlim synchronizationSemaphore = new(1);
    protected CancellationToken activeSynchronizationCancellationToken;

    protected List<IRequestBase> changeRequestQueue = [];
    protected readonly IMessenger Messenger;
    
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
                SynchronizationStatus));
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
            if (TotalItemsToSync == 0 || RemainingItemsToSync == 0)
                return -1; // Indeterminate

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
            SynchronizationStatus));
    }

    /// <summary>
    /// Queues a single request to be executed in the next synchronization.
    /// </summary>
    /// <param name="request">Request to execute.</param>
    public void QueueRequest(IRequestBase request) => changeRequestQueue.Add(request);

    /// <summary>
    /// Runs existing queued requests in the queue.
    /// </summary>
    /// <param name="batchedRequests">Batched requests to execute. Integrator methods will only receive batched requests.</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public abstract Task ExecuteNativeRequestsAsync(List<IExecutableRequest> batchedRequests, CancellationToken cancellationToken = default);

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

    public List<IExecutableRequest> ForEachRequest<TWinoRequestType>(IEnumerable<TWinoRequestType> requests,
                Func<TWinoRequestType, TBaseRequest> action)
                where TWinoRequestType : IRequestBase
    {
        List<IExecutableRequest> ret = [];

        foreach (var request in requests)
            ret.Add(new HttpRequestBundle<TBaseRequest>(action(request), request, request) as IExecutableRequest);

        return ret;
    }
}
