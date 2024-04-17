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

        public static readonly DependencyProperty StateProperty = DependencyProperty.Register(nameof(State), typeof(AccountCreationDialogState), typeof(BaseAccountCreationDialog), new PropertyMetadata(AccountCreationDialogState.Idle, OnStateChanged));

        // Prevent users from dismissing it by ESC key.
        private void DialogClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
        {
            if (args.Result == ContentDialogResult.None)
            {
                args.Cancel = true;
            }
        }

        public void ShowDialog()
        {
            _ = ShowAsync();
        }

        public void Complete()
        {
            State = AccountCreationDialogState.Completed;

            // Unregister from closing event.
            Closing -= DialogClosing;

            Hide();
        }

        private static void OnStateChanged(DependencyObject obj, DependencyPropertyChangedEventArgs args)
        {
            if (obj is BaseAccountCreationDialog dialog)
            {
                dialog.UpdateState();
            }
        }

        public abstract void UpdateState();
    }
}
