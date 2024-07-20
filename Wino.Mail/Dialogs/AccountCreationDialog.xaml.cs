using Wino.Core.Domain.Enums;
using Wino.Helpers;

namespace Wino.Dialogs
{
    public sealed partial class AccountCreationDialog : BaseAccountCreationDialog
    {
        public AccountCreationDialog()
        {
            InitializeComponent();
        }

        public override void UpdateState()
        {
            switch (State)
            {
                case AccountCreationDialogState.SigningIn:
                    StatusText.Text = "Account information is being saved.";
                    DialogIcon.Data = XamlHelpers.GetPathIcon("SavingAccountPathIcon");
                    break;
                case AccountCreationDialogState.PreparingFolders:
                    StatusText.Text = "We are getting folder information at the moment.";
                    DialogIcon.Data = XamlHelpers.GetPathIcon("PreparingFoldersPathIcon");
                    break;
                case AccountCreationDialogState.Completed:
                    StatusText.Text = "All done.";
                    break;
                default:
                    break;
            }
        }
    }
}
