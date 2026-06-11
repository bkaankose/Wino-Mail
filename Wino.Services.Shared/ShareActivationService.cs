#nullable enable
using System;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Launch;

namespace Wino.Services;

public class ShareActivationService : IShareActivationService
{
    private readonly object _syncRoot = new();
    private MailShareRequest? _pendingShareRequest;
    private PendingComposeMailShareRequest? _pendingComposeShareRequest;

    public MailShareRequest? PendingShareRequest
    {
        get
        {
            lock (_syncRoot)
            {
                return _pendingShareRequest;
            }
        }
        set
        {
            lock (_syncRoot)
            {
                _pendingShareRequest = value;
            }
        }
    }

    public MailShareRequest? ConsumePendingShareRequest()
    {
        lock (_syncRoot)
        {
            var pendingRequest = _pendingShareRequest;
            _pendingShareRequest = null;
            return pendingRequest;
        }
    }

    public void ClearPendingShareRequest()
    {
        lock (_syncRoot)
        {
            _pendingShareRequest = null;
        }
    }

    public void StagePendingComposeShareRequest(Guid draftUniqueId, MailShareRequest shareRequest)
    {
        lock (_syncRoot)
        {
            _pendingComposeShareRequest = new PendingComposeMailShareRequest(draftUniqueId, shareRequest);
        }
    }

    public MailShareRequest? ConsumePendingComposeShareRequest(Guid draftUniqueId)
    {
        lock (_syncRoot)
        {
            if (_pendingComposeShareRequest?.DraftUniqueId != draftUniqueId)
                return null;

            var pendingRequest = _pendingComposeShareRequest.ShareRequest;
            _pendingComposeShareRequest = null;
            return pendingRequest;
        }
    }
}
