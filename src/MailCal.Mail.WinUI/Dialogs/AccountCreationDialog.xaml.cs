using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Interfaces;

namespace MailCal.Dialogs;

public sealed partial class AccountCreationDialog : ContentDialog, IAccountCreationDialog
{
    private readonly Func<ContentDialog, Task<bool>?> _startPresentation;
    public CancellationTokenSource? CancellationTokenSource { get; private set; }

    public AccountCreationDialogState State
    {
        get { return (AccountCreationDialogState)GetValue(StateProperty); }
        set { SetValue(StateProperty, value); }
    }

    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(nameof(State), typeof(AccountCreationDialogState), typeof(AccountCreationDialog), new PropertyMetadata(AccountCreationDialogState.Idle));

    /// <param name="startPresentation">The dialog service's presentation for progress dialogs.</param>
    public AccountCreationDialog(Func<ContentDialog, Task<bool>?> startPresentation)
    {
        _startPresentation = startPresentation;

        InitializeComponent();
    }

    // Prevent users from dismissing it by ESC key.
    public void DialogClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        if (args.Result == ContentDialogResult.None)
        {
            args.Cancel = true;
        }
    }

    public void Complete(bool cancel)
    {
        State = cancel ? AccountCreationDialogState.Canceled : AccountCreationDialogState.Completed;

        // Unregister from closing event.
        Closing -= DialogClosing;

        if (cancel && CancellationTokenSource != null && !CancellationTokenSource.IsCancellationRequested)
        {
            CancellationTokenSource.Cancel();
        }

        Hide();
    }

    private void CancelClicked(object sender, System.EventArgs e) => Complete(true);

    public async Task ShowDialogAsync(CancellationTokenSource cancellationTokenSource)
    {
        CancellationTokenSource = cancellationTokenSource;

        // Account creation continues without the progress surface when another dialog owns the window.
        var opened = _startPresentation(this);
        if (opened != null)
            await opened;
    }
}
