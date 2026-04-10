using System;
using Wino.Mail.WinUI.Models;

namespace Wino.Mail.WinUI.Interfaces;

public interface IPopoutClient
{
    bool SupportsPopOut { get; }
    event EventHandler<PopOutRequestedEventArgs> PopOutRequested;
    event EventHandler<PopoutHostActionRequestedEventArgs> HostActionRequested;
    HostedPopoutDescriptor GetPopoutDescriptor();
    void OnPopoutStateChanged(bool isPoppedOut);
}
