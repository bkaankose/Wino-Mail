using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.WinUI.Models;
using Wino.Mail.WinUI.Services;
using WinUIEx;

namespace Wino.Mail.WinUI;

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
            WinoApplication.Current.Services.GetRequiredService<IKeyboardShortcutService>(),
            WinoApplication.Current.Services.GetRequiredService<IWinoLogger>(),
            () => WinoApplicationMode.Mail,
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

        this.SetIcon("Assets/Wino_Icon.ico");
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
