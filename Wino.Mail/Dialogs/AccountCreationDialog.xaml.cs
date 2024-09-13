namespace Wino.Dialogs
{
    public sealed partial class AccountCreationDialog : BaseAccountCreationDialog
    {
        public AccountCreationDialog()
        {
            InitializeComponent();
        }

        private void CancelClicked(object sender, Windows.UI.Xaml.RoutedEventArgs e)
        {
            Complete(true);
        }
    }
}
