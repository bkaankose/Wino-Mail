using System;
using System.Text.RegularExpressions;
using Windows.UI.Xaml.Controls;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;

namespace Wino.Dialogs;

public sealed partial class SignatureEditorDialog : ContentDialog
{
    public AccountSignature Result;

    public SignatureEditorDialog()
    {
        InitializeComponent();

        SignatureNameTextBox.Header = Translator.SignatureEditorDialog_SignatureName_TitleNew;

        // TODO: Should be added additional logic to enable/disable primary button when webview content changed.
        IsPrimaryButtonEnabled = true;
    }

    public SignatureEditorDialog(AccountSignature signatureModel)
    {
        InitializeComponent();

        SignatureNameTextBox.Text = signatureModel.Name.Trim();
        SignatureNameTextBox.Header = string.Format(Translator.SignatureEditorDialog_SignatureName_TitleEdit, signatureModel.Name);

        Result = new AccountSignature
        {
            Id = signatureModel.Id,
            Name = signatureModel.Name,
            MailAccountId = signatureModel.MailAccountId,
            HtmlBody = signatureModel.HtmlBody
        };

        // TODO: Should be added additional logic to enable/disable primary button when webview content changed.
        IsPrimaryButtonEnabled = true;
    }

    private async void SignatureDialogOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        await WebViewEditor.RenderHtmlAsync(Result?.HtmlBody ?? string.Empty);
    }

    private void DialogClosed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        WebViewEditor.Dispose();
    }

    private async void SaveClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var newSignature = Regex.Unescape(await WebViewEditor.GetHtmlBodyAsync());

        if (Result == null)
        {
            Result = new AccountSignature
            {
                Id = Guid.NewGuid(),
                Name = SignatureNameTextBox.Text.Trim(),
                HtmlBody = newSignature
            };
        }
        else
        {
            Result.Name = SignatureNameTextBox.Text.Trim();
            Result.HtmlBody = newSignature;
        }

        Hide();
    }

    private void CancelClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        Hide();
    }

    private void SignatureNameTextBoxTextChanged(object sender, TextChangedEventArgs e) => IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(SignatureNameTextBox.Text);
}
