using System;

namespace Wino.Mail.WinUI.Models;

public sealed class PopoutHostActionRequestedEventArgs : EventArgs
{
    public PopoutHostActionRequestedEventArgs(PopoutHostActionKind actionKind, Type? targetPageType = null, Guid? targetMailUniqueId = null)
    {
        ActionKind = actionKind;
        TargetPageType = targetPageType;
        TargetMailUniqueId = targetMailUniqueId;
    }

    public PopoutHostActionKind ActionKind { get; }
    public Type? TargetPageType { get; }
    public Guid? TargetMailUniqueId { get; }
}
