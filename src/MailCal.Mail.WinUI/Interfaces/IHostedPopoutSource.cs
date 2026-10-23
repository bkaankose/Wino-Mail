using Microsoft.UI.Xaml;
using MailCal.Mail.WinUI;
using MailCal.Mail.WinUI.Models;

namespace MailCal.Mail.WinUI.Interfaces;

public interface IHostedPopoutSource
{
    bool CanPopOutCurrentContent();
    FrameworkElement? GetCurrentHostedContent();
    HostedPopoutDescriptor CreatePopoutDescriptor(IPopoutClient client);
    FrameworkElement DetachHostedContent();
    void OnHostedContentPoppedOut(FrameworkElement content, HostedContentPopoutWindow window, HostedPopoutDescriptor descriptor);
    void OnHostedPopoutClosed(FrameworkElement content, HostedPopoutDescriptor descriptor);
}
