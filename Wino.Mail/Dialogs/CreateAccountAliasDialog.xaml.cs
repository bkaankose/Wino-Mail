using System;
using Windows.UI.Xaml.Controls;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Interfaces;

namespace Wino.Dialogs;

public sealed partial class CreateAccountAliasDialog : ContentDialog, ICreateAccountAliasDialog
{
    public MailAccountAlias CreatedAccountAlias { get; set; }
    public CreateAccountAliasDialog()
    {
        InitializeComponent();
    }

    private void CreateClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        CreatedAccountAlias = new MailAccountAlias
        {
            AliasAddress = AliasTextBox.Text.Trim(),
            ReplyToAddress = ReplyToTextBox.Text.Trim(),
            Id = Guid.NewGuid(),
            IsPrimary = false,
            IsVerified = false
        };

        Hide();
    }
}
