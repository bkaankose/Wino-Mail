using Microsoft.UI.Xaml;
using Wino.Mail.WinUI;
using Wino.Mail.WinUI.Models;

namespace Wino.Mail.WinUI.Interfaces;

public interface IHostedPopoutSource
{
    bool CanPopOutCurrentContent();
    FrameworkElement? GetCurrentHostedContent();
    HostedPopoutDescriptor CreatePopoutDescriptor(IPopoutClient client);
    FrameworkElement DetachHostedContent();
    void OnHostedContentPoppedOut(FrameworkElement content, HostedContentPopoutWindow window, HostedPopoutDescriptor descriptor);
    void OnHostedPopoutClosed(FrameworkElement content, HostedPopoutDescriptor descriptor);
}
