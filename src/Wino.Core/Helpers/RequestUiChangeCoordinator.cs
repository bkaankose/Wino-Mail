using System;
using System.Collections.Generic;
using System.Linq;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Requests.Mail;

namespace Wino.Core.Helpers;

/// <summary>
/// Applies, reverts and completes the in-memory UI changes of queued requests, once each, and sends their messages
/// through the sender the container composed it with.
/// </summary>
public sealed class RequestUiChangeCoordinator
{
    private readonly Dictionary<IUIChangeRequest, RequestUiChangeState> _requestStates = new(ReferenceEqualityComparer<IUIChangeRequest>.Instance);
    private readonly object _stateLock = new();
    private readonly IUIMessageSender _sender;

    public RequestUiChangeCoordinator(IUIMessageSender sender)
    {
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
    }

    public void ApplyRequests(IEnumerable<IRequestBase> requests, Func<IRequestBase, bool> shouldApply = null)
    {
        var requestList = requests?
            .Where(request => request != null && (shouldApply?.Invoke(request) ?? true))
            .Where(TryBeginApply)
            .ToList() ?? [];

        if (requestList.Count == 0)
            return;

        foreach (var group in requestList.GroupBy(request => request.GroupingKey()))
        {
            var groupRequests = group.ToList();

            if (groupRequests.Count > 1 && TryApplyBatchUiChanges(groupRequests, apply: true))
            {
                MarkApplied(groupRequests);
                continue;
            }

            foreach (var request in groupRequests)
            {
                request.ApplyUIChanges(_sender);
                MarkApplied(request);
            }
        }
    }

    public void RevertRequests(IEnumerable<IRequestBase> requests, Func<IRequestBase, bool> shouldRevert = null)
    {
        var requestList = requests?
            .Where(request => request != null && (shouldRevert?.Invoke(request) ?? true))
            .Where(TryBeginRevert)
            .ToList() ?? [];

        if (requestList.Count == 0)
            return;

        foreach (var group in requestList.GroupBy(request => request.GroupingKey()))
        {
            var groupRequests = group.ToList();

            if (groupRequests.Count > 1 && TryApplyBatchUiChanges(groupRequests, apply: false))
            {
                MarkReverted(groupRequests);
                continue;
            }

            foreach (var request in groupRequests)
            {
                request.RevertUIChanges(_sender);
                MarkReverted(request);
            }
        }
    }

    public void ApplyBundles<TRequest>(IEnumerable<IRequestBundle<TRequest>> bundles, Func<IRequestBase, bool> shouldApply = null)
    {
        var bundleList = bundles?
            .Where(bundle => bundle?.Request != null && (shouldApply?.Invoke(bundle.Request) ?? true))
            .ToList() ?? [];

        var directUiChanges = bundleList
            .Where(bundle => bundle.UIChangeRequest != null && !ReferenceEquals(bundle.UIChangeRequest, bundle.Request))
            .Select(bundle => bundle.UIChangeRequest)
            .Where(TryBeginApply)
            .ToList();

        foreach (var uiChange in directUiChanges)
        {
            uiChange.ApplyUIChanges(_sender);
            MarkApplied(uiChange);
        }

        ApplyRequests(bundleList
            .Where(bundle => ReferenceEquals(bundle.UIChangeRequest, bundle.Request) || bundle.UIChangeRequest == null)
            .Select(bundle => bundle.Request),
            shouldApply);
    }

    public void RevertBundle<TRequest>(IRequestBundle<TRequest> bundle)
        => RevertBundle((IRequestBundle)bundle);

    public void RevertBundle(IRequestBundle bundle)
    {
        if (bundle?.UIChangeRequest == null)
            return;

        if (!TryBeginRevert(bundle.UIChangeRequest))
            return;

        bundle.UIChangeRequest.RevertUIChanges(_sender);
        MarkReverted(bundle.UIChangeRequest);
    }

    public void RevertRequest(IUIChangeRequest request)
    {
        if (request == null || !TryBeginRevert(request))
            return;

        request.RevertUIChanges(_sender);
        MarkReverted(request);
    }

    /// <summary>
    /// Releases lifecycle tracking after a request has either been committed or reverted.
    /// Request queues are intentionally in-memory, so completed entries must not retain the
    /// entity snapshots carried by contact and task mutations.
    /// </summary>
    public void CompleteRequests(IEnumerable<IRequestBase> requests)
    {
        if (requests is null)
            return;

        lock (_stateLock)
        {
            foreach (var request in requests.Where(request => request is not null))
                _requestStates.Remove(request);
        }
    }

    private bool TryApplyBatchUiChanges(IReadOnlyList<IRequestBase> requests, bool apply)
    {
        if (requests == null || requests.Count <= 1)
            return false;

        IUIChangeRequest request = requests[0] switch
        {
            MarkReadRequest => new BatchMarkReadRequest(requests.Cast<MarkReadRequest>()),
            ChangeFlagRequest => new BatchChangeFlagRequest(requests.Cast<ChangeFlagRequest>()),
            DeleteRequest => new BatchDeleteRequest(requests.Cast<DeleteRequest>()),
            MoveRequest => new BatchMoveRequest(requests.Cast<MoveRequest>()),
            ArchiveRequest => new BatchArchiveRequest(requests.Cast<ArchiveRequest>()),
            ChangeJunkStateRequest => new BatchChangeJunkStateRequest(requests.Cast<ChangeJunkStateRequest>()),
            _ => null
        };

        if (request == null)
            return false;

        if (apply)
        {
            request.ApplyUIChanges(_sender);
        }
        else
        {
            request.RevertUIChanges(_sender);
        }

        return true;
    }

    private bool TryBeginApply(IUIChangeRequest request)
    {
        lock (_stateLock)
        {
            if (!_requestStates.TryGetValue(request, out var state))
            {
                _requestStates[request] = new RequestUiChangeState { IsApplying = true };
                return true;
            }

            if (state.IsApplied || state.IsApplying || state.IsReverted)
                return false;

            state.IsApplying = true;
            return true;
        }
    }

    private void MarkApplied(IEnumerable<IUIChangeRequest> requests)
    {
        foreach (var request in requests)
            MarkApplied(request);
    }

    private void MarkApplied(IUIChangeRequest request)
    {
        lock (_stateLock)
        {
            if (!_requestStates.TryGetValue(request, out var state))
            {
                state = new RequestUiChangeState();
                _requestStates[request] = state;
            }

            state.IsApplying = false;
            state.IsApplied = true;
        }
    }

    private bool TryBeginRevert(IUIChangeRequest request)
    {
        lock (_stateLock)
        {
            if (!_requestStates.TryGetValue(request, out var state))
                return false;

            if (!state.IsApplied || state.IsReverted || state.IsReverting)
                return false;

            state.IsReverting = true;
            return true;
        }
    }

    private void MarkReverted(IEnumerable<IUIChangeRequest> requests)
    {
        foreach (var request in requests)
            MarkReverted(request);
    }

    private void MarkReverted(IUIChangeRequest request)
    {
        lock (_stateLock)
        {
            if (!_requestStates.TryGetValue(request, out var state))
            {
                state = new RequestUiChangeState();
                _requestStates[request] = state;
            }

            state.IsReverting = false;
            state.IsReverted = true;
        }
    }

    private sealed class RequestUiChangeState
    {
        public bool IsApplying { get; set; }
        public bool IsApplied { get; set; }
        public bool IsReverting { get; set; }
        public bool IsReverted { get; set; }
    }

    private sealed class ReferenceEqualityComparer<T> : IEqualityComparer<T> where T : class
    {
        public static ReferenceEqualityComparer<T> Instance { get; } = new();

        public bool Equals(T x, T y) => ReferenceEquals(x, y);

        public int GetHashCode(T obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}
