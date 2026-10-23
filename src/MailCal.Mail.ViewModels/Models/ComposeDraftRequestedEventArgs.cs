using System;

namespace MailCal.Mail.ViewModels.Models;

public sealed class ComposeDraftRequestedEventArgs : EventArgs
{
    public ComposeDraftRequestedEventArgs(Guid draftUniqueId)
    {
        DraftUniqueId = draftUniqueId;
    }

    public Guid DraftUniqueId { get; }
}
