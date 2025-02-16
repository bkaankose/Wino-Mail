using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Messaging.UI;


namespace Wino.Core.UWP.Controls;

public sealed partial class AccountCreationDialogControl : UserControl, IRecipient<CopyAuthURLRequested>
{
    private string copyClipboardURL;

    public event EventHandler CancelClicked;

    public AccountCreationDialogState State
    {
        get { return (AccountCreationDialogState)GetValue(StateProperty); }
        set { SetValue(StateProperty, value); }
    }

    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(nameof(State), typeof(AccountCreationDialogState), typeof(AccountCreationDialogControl), new PropertyMetadata(AccountCreationDialogState.Idle, new PropertyChangedCallback(OnStateChanged)));

    public AccountCreationDialogControl()
    {
        InitializeComponent();
    }

    private static void OnStateChanged(DependencyObject obj, DependencyPropertyChangedEventArgs args)
    {
        if (obj is AccountCreationDialogControl dialog)
        {
            dialog.UpdateVisualStates();
        }
    }

    private void UpdateVisualStates() => VisualStateManager.GoToState(this, State.ToString(), false);

    public async void Receive(CopyAuthURLRequested message)
    {
        copyClipboardURL = message.AuthURL;

        await Task.Delay(2000);

        await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
        {
            AuthHelpDialogButton.Visibility = Windows.UI.Xaml.Visibility.Visible;
        });
    }

    private void ControlLoaded(object sender, Windows.UI.Xaml.RoutedEventArgs e)
    {
        WeakReferenceMessenger.Default.Register(this);
    }

    private void ControlUnloaded(object sender, Windows.UI.Xaml.RoutedEventArgs e)
    {
        WeakReferenceMessenger.Default.UnregisterAll(this);
    }

    private async void CopyClicked(object sender, Windows.UI.Xaml.RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(copyClipboardURL)) return;

        var clipboardService = WinoApplication.Current.Services.GetService<IClipboardService>();
        await clipboardService.CopyClipboardAsync(copyClipboardURL);
    }


    private void CancelButtonClicked(object sender, Windows.UI.Xaml.RoutedEventArgs e) => CancelClicked?.Invoke(this, null);
}
