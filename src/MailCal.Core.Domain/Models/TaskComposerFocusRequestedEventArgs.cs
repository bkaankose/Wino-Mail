using System;

namespace MailCal.Core.Domain.Models;

public sealed class TaskComposerFocusRequestedEventArgs : EventArgs
{
    public static TaskComposerFocusRequestedEventArgs Default { get; } = new();

    private TaskComposerFocusRequestedEventArgs()
    {
    }
}
