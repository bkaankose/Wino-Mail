using System;
using Microsoft.UI.Xaml;
using Wino.Mail.WinUI.Models;
using WinUIEx;

namespace Wino.Mail.WinUI;

public sealed partial class HostedContentPopoutWindow : WindowEx
{
    private readonly Action _closedCallback;

    public HostedPopoutDescriptor Descriptor { get; }

    public HostedContentPopoutWindow(HostedPopoutDescriptor descriptor, Action closedCallback)
    {
        Descriptor = descriptor;
        _closedCallback = closedCallback;

        InitializeComponent();

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
        _closedCallback();
    }
}
