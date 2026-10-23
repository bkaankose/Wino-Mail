using System;
using Microsoft.UI.Xaml.Controls;
using MailCal.Core.Domain.Entities.Mail;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Interfaces;

namespace MailCal.Dialogs;

public sealed partial class CreateAccountAliasDialog : ContentDialog, ICreateAccountAliasDialog
{
    public MailAccountAlias CreatedAccountAlias { get; set; } = null!;
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
            IsVerified = false,
            Source = AliasSource.Manual,
            SendCapability = AliasSendCapability.Unknown
        };

        Hide();
    }
}
