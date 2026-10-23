using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Interfaces;
using MailCal.Mail.WinUI.Models;
using MailCal.Mail.WinUI.Services;
using WinUIEx;

namespace MailCal.Mail.WinUI;

public sealed partial class HostedContentPopoutWindow : WindowEx
{
    private readonly Action _closedCallback;
    private readonly KeyboardShortcutController _shortcutController;

    public HostedPopoutDescriptor Descriptor { get; }

    public HostedContentPopoutWindow(HostedPopoutDescriptor descriptor, Action closedCallback)
    {
        Descriptor = descriptor;
        _closedCallback = closedCallback;

        InitializeComponent();

        _shortcutController = new KeyboardShortcutController(
            RootGrid,
            MailCalApplication.Current.Services.GetRequiredService<IKeyboardShortcutService>(),
            MailCalApplication.Current.Services.GetRequiredService<IMailCalLogger>(),
            () => MailCalApplicationMode.Mail,
            GetHostedPage,
            GetHostedPage,
            _ => System.Threading.Tasks.Task.CompletedTask,
            isPopOut: true);

        Title = descriptor.Title;
        Width = descriptor.Width;
        Height = descriptor.Height;
        MinWidth = descriptor.MinWidth;
        MinHeight = descriptor.MinHeight;

        ExtendsContentIntoTitleBar = true;

        this.SetIcon("Assets/MailCal_Icon.ico");
        this.CenterOnScreen();

        Closed += OnClosed;
    }

    public void SetHostedContent(FrameworkElement content)
    {
        ContentHost.Children.Clear();
        ContentHost.Children.Add(content);
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        Closed -= OnClosed;
        _shortcutController.Dispose();
        _closedCallback();
    }

    private BasePage? GetHostedPage()
        => ContentHost.Children.Count == 1 ? ContentHost.Children[0] as BasePage : null;
}
