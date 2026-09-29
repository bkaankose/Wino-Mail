using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain.Enums;


namespace Wino.Mail.WinUI.Controls;

public sealed partial class AccountCreationDialogControl : UserControl
{
    public event EventHandler? CancelClicked;

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

    private void CancelButtonClicked(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => CancelClicked?.Invoke(this, EventArgs.Empty);
}
