#nullable enable
using System;
using Wino.Core.Domain.Models.Launch;

namespace Wino.Core.Domain.Interfaces;

public interface IShareActivationService
{
    MailShareRequest? PendingShareRequest { get; set; }
    MailShareRequest? ConsumePendingShareRequest();
    void ClearPendingShareRequest();
    void StagePendingComposeShareRequest(Guid draftUniqueId, MailShareRequest shareRequest);
    MailShareRequest? ConsumePendingComposeShareRequest(Guid draftUniqueId);
}
