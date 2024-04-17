using System;
using System.Collections.Generic;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Wino.Core.Domain.Enums;
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

        public Tuple<string, MailProviderType> AccountInformationTuple = null;

        public NewAccountDialog()
        {
            InitializeComponent();

            // AccountColorPicker.Color = Colors.Blue;
        }

        private static void OnSelectedProviderChanged(DependencyObject obj, DependencyPropertyChangedEventArgs args)
        {
            if (obj is NewAccountDialog dialog)
                dialog.ValidateCreateButton();
        }

        private void CancelClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            Hide();
        }

        private void CreateClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            ValidateCreateButton();

            if (IsSecondaryButtonEnabled)
            {
                AccountInformationTuple = new Tuple<string, MailProviderType>(AccountNameTextbox.Text.Trim(), SelectedMailProvider.Type);
                Hide();
            }
        }

        private void AccountNameChanged(object sender, TextChangedEventArgs e)
        {
            ValidateCreateButton();
        }

        // Returns whether we can create account or not.
        private void ValidateCreateButton()
        {
            bool shouldEnable = SelectedMailProvider != null
                && SelectedMailProvider.IsSupported
                && !string.IsNullOrEmpty(AccountNameTextbox.Text);

            IsPrimaryButtonEnabled = shouldEnable;
        }

        private void DialogOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
        {
            ValidateCreateButton();
        }
    }
}
