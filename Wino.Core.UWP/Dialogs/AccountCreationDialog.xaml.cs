using System.Threading;
using System.Threading.Tasks;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Dialogs
{
    public sealed partial class AccountCreationDialog : ContentDialog, IAccountCreationDialog
    {
        private TaskCompletionSource<bool> dialogOpened = new TaskCompletionSource<bool>();
        public CancellationTokenSource CancellationTokenSource { get; private set; }

        public AccountCreationDialogState State
        {
            get { return (AccountCreationDialogState)GetValue(StateProperty); }
            set { SetValue(StateProperty, value); }
        }

        public static readonly DependencyProperty StateProperty = DependencyProperty.Register(nameof(State), typeof(AccountCreationDialogState), typeof(AccountCreationDialog), new PropertyMetadata(AccountCreationDialogState.Idle));

        public AccountCreationDialog()
        {
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

            if (cancel && !CancellationTokenSource.IsCancellationRequested)
            {
                CancellationTokenSource.Cancel();
            }

            Hide();
        }

        private void CancelClicked(object sender, System.EventArgs e) => Complete(true);

        public async Task ShowDialogAsync(CancellationTokenSource cancellationTokenSource)
        {
            CancellationTokenSource = cancellationTokenSource;

            Opened += DialogOpened;
            _ = ShowAsync();

            await dialogOpened.Task;
        }

        private void DialogOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
        {
            Opened -= DialogOpened;

            dialogOpened?.SetResult(true);
        }
    }
}
