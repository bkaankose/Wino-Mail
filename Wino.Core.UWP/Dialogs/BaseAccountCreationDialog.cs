using System.Threading;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Dialogs
{
    public abstract class BaseAccountCreationDialog : ContentDialog, IAccountCreationDialog
    {
        public AccountCreationDialogState State
        {
            get { return (AccountCreationDialogState)GetValue(StateProperty); }
            set { SetValue(StateProperty, value); }
        }

        public CancellationTokenSource CancellationTokenSource { get; private set; }

        public static readonly DependencyProperty StateProperty = DependencyProperty.Register(nameof(State), typeof(AccountCreationDialogState), typeof(BaseAccountCreationDialog), new PropertyMetadata(AccountCreationDialogState.Idle));

        // Prevent users from dismissing it by ESC key.
        public void DialogClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
        {
            if (args.Result == ContentDialogResult.None)
            {
                args.Cancel = true;
            }
        }

        public void ShowDialog(CancellationTokenSource cancellationTokenSource)
        {
            CancellationTokenSource = cancellationTokenSource;

            _ = ShowAsync();
        }

        public void Complete(bool cancel)
        {
            State = cancel ? AccountCreationDialogState.Canceled : AccountCreationDialogState.Completed;

            // Unregister from closing event.
            Closing -= DialogClosing;

            if (cancel && !CancellationTokenSource.IsCancellationRequested)
            {
                CancellationTokenSource.Cancel();
            }

            Hide();
        }
    }
}
