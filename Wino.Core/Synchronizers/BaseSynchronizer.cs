using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Requests.Bundles;
using Wino.Messaging.UI;

namespace Wino.Core.Synchronizers;

public abstract class BaseSynchronizer<TBaseRequest> : IBaseSynchronizer
{
    protected SemaphoreSlim synchronizationSemaphore = new(1);
    protected CancellationToken activeSynchronizationCancellationToken;

    protected List<IRequestBase> changeRequestQueue = [];
    public MailAccount Account { get; }

    private AccountSynchronizerState state;
    public AccountSynchronizerState State
    {
        get { return state; }
        set
        {
            state = value;

            WeakReferenceMessenger.Default.Send(new AccountSynchronizerStateChanged(Account.Id, value));
        }
    }

    protected BaseSynchronizer(MailAccount account)
    {
        Account = account;
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
}
