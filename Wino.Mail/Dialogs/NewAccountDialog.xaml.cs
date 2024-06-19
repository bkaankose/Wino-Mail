using System.Collections.Generic;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;

namespace Wino.Dialogs
{
    public sealed partial class NewAccountDialog : ContentDialog
    {
        /// <summary>
        /// Gets or sets current selected mail provider in the dialog.
        /// </summary>
        public ProviderDetail SelectedMailProvider
        {
            get { return (ProviderDetail)GetValue(SelectedMailProviderProperty); }
            set { SetValue(SelectedMailProviderProperty, value); }
        }

        public static readonly DependencyProperty SelectedMailProviderProperty = DependencyProperty.Register(nameof(SelectedMailProvider), typeof(ProviderDetail), typeof(NewAccountDialog), new PropertyMetadata(null, new PropertyChangedCallback(OnSelectedProviderChanged)));

        // List of available mail providers for now.

        public List<IProviderDetail> Providers { get; set; }

        public AccountCreationDialogResult Result = null;

        public NewAccountDialog()
        {
            InitializeComponent();

            // AccountColorPicker.Color = Colors.Blue;
        }

        private static void OnSelectedProviderChanged(DependencyObject obj, DependencyPropertyChangedEventArgs args)
        {
            if (obj is NewAccountDialog dialog)
                dialog.Validate();
        }

        private void CancelClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            Hide();
        }

        private void CreateClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            Validate();

            if (IsSecondaryButtonEnabled)
            {
                Result = new AccountCreationDialogResult(SelectedMailProvider.Type, AccountNameTextbox.Text.Trim(), SenderNameTextbox.Text.Trim());
                Hide();
            }
        }

        private void AccountNameChanged(object sender, TextChangedEventArgs e) => Validate();
        private void SenderNameChanged(object sender, TextChangedEventArgs e) => Validate();

        private void Validate()
        {
            ValidateCreateButton();
            ValidateNames();
        }

        // Returns whether we can create account or not.
        private void ValidateCreateButton()
        {
            bool shouldEnable = SelectedMailProvider != null
                && SelectedMailProvider.IsSupported
                && !string.IsNullOrEmpty(AccountNameTextbox.Text)
                && (SelectedMailProvider.RequireSenderNameOnCreationDialog ? !string.IsNullOrEmpty(SenderNameTextbox.Text) : true);

            IsPrimaryButtonEnabled = shouldEnable;
        }

        private void ValidateNames()
        {
            AccountNameTextbox.IsEnabled = SelectedMailProvider != null;
            SenderNameTextbox.IsEnabled = SelectedMailProvider != null && SelectedMailProvider.Type != Core.Domain.Enums.MailProviderType.IMAP4;
        }

        private void DialogOpened(ContentDialog sender, ContentDialogOpenedEventArgs args) => Validate();
    }
}
