using System;
using MailCal.Mail.WinUI.Models;

namespace MailCal.Mail.WinUI.Interfaces;

public interface IPopoutClient
{
    bool SupportsPopOut { get; }
    event EventHandler<PopOutRequestedEventArgs> PopOutRequested;
    event EventHandler<PopoutHostActionRequestedEventArgs> HostActionRequested;
    HostedPopoutDescriptor GetPopoutDescriptor();
    void OnPopoutStateChanged(bool isPoppedOut);
}
