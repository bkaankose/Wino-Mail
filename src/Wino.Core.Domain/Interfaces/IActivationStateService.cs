#nullable enable
using System;
using Wino.Core.Domain.Models.Launch;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Holds what the app was activated with until the shell consumes it:
/// toast launch parameters, mailto URIs and share-target requests.
/// </summary>
public interface IActivationStateService
{
    /// <summary>
    /// Used to handle toasts.
    /// </summary>
    object? LaunchParameter { get; set; }

    /// <summary>
    /// Used to handle mailto links.
    /// </summary>
    MailToUri? MailToUri { get; set; }

    MailShareRequest? PendingShareRequest { get; set; }
    MailShareRequest? ConsumePendingShareRequest();
    void ClearPendingShareRequest();
    void StagePendingComposeShareRequest(Guid draftUniqueId, MailShareRequest shareRequest);
    MailShareRequest? ConsumePendingComposeShareRequest(Guid draftUniqueId);
}
