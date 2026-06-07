using System;
using System.Collections.Generic;
using System.Linq;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Requests.Mail;

namespace Wino.Core.Helpers;

public static class RequestUiChangeCoordinator
{
    private static readonly Dictionary<IUIChangeRequest, RequestUiChangeState> RequestStates = new(ReferenceEqualityComparer<IUIChangeRequest>.Instance);
    private static readonly object StateLock = new();

    public static void ApplyRequests(IEnumerable<IRequestBase> requests, Func<IRequestBase, bool> shouldApply = null)
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
                request.ApplyUIChanges();
                MarkApplied(request);
            }
        }
    }

    public static void RevertRequests(IEnumerable<IRequestBase> requests, Func<IRequestBase, bool> shouldRevert = null)
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
                request.RevertUIChanges();
                MarkReverted(request);
            }
        }
    }

    public static void ApplyBundles<TRequest>(IEnumerable<IRequestBundle<TRequest>> bundles, Func<IRequestBase, bool> shouldApply = null)
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
            uiChange.ApplyUIChanges();
            MarkApplied(uiChange);
        }

        ApplyRequests(bundleList
            .Where(bundle => ReferenceEquals(bundle.UIChangeRequest, bundle.Request) || bundle.UIChangeRequest == null)
            .Select(bundle => bundle.Request),
            shouldApply);
    }

    public static void RevertBundle<TRequest>(IRequestBundle<TRequest> bundle)
        => RevertBundle((IRequestBundle)bundle);

    public static void RevertBundle(IRequestBundle bundle)
    {
        if (bundle?.UIChangeRequest == null)
            return;

        if (!TryBeginRevert(bundle.UIChangeRequest))
            return;

        bundle.UIChangeRequest.RevertUIChanges();
        MarkReverted(bundle.UIChangeRequest);
    }

    public static void RevertRequest(IUIChangeRequest request)
    {
        if (request == null || !TryBeginRevert(request))
            return;

        request.RevertUIChanges();
        MarkReverted(request);
    }

    private static bool TryApplyBatchUiChanges(IReadOnlyList<IRequestBase> requests, bool apply)
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
            request.ApplyUIChanges();
        }
        else
        {
            request.RevertUIChanges();
        }

        return true;
    }

    private static bool TryBeginApply(IUIChangeRequest request)
    {
        lock (StateLock)
        {
            if (!RequestStates.TryGetValue(request, out var state))
            {
                RequestStates[request] = new RequestUiChangeState { IsApplying = true };
                return true;
            }

            if (state.IsApplied || state.IsApplying || state.IsReverted)
                return false;

            state.IsApplying = true;
            return true;
        }
    }

    private static void MarkApplied(IEnumerable<IUIChangeRequest> requests)
    {
        foreach (var request in requests)
            MarkApplied(request);
    }

    private static void MarkApplied(IUIChangeRequest request)
    {
        lock (StateLock)
        {
            if (!RequestStates.TryGetValue(request, out var state))
            {
                state = new RequestUiChangeState();
                RequestStates[request] = state;
            }

            state.IsApplying = false;
            state.IsApplied = true;
        }
    }

    private static bool TryBeginRevert(IUIChangeRequest request)
    {
        lock (StateLock)
        {
            if (!RequestStates.TryGetValue(request, out var state))
                return false;

            if (!state.IsApplied || state.IsReverted || state.IsReverting)
                return false;

            state.IsReverting = true;
            return true;
        }
    }

    private static void MarkReverted(IEnumerable<IUIChangeRequest> requests)
    {
        foreach (var request in requests)
            MarkReverted(request);
    }

    private static void MarkReverted(IUIChangeRequest request)
    {
        lock (StateLock)
        {
            if (!RequestStates.TryGetValue(request, out var state))
            {
                state = new RequestUiChangeState();
                RequestStates[request] = state;
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
