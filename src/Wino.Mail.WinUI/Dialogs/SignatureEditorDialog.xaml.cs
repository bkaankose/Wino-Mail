using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.WinUI;
using Wino.Mail.WinUI.Helpers;

namespace Wino.Mail.WinUI.Dialogs;

public sealed partial class SignatureEditorDialog : ContentDialog
{
    public AccountSignature Result = null!;

    public SignatureEditorDialog()
    {
        InitializeComponent();
        SignatureEditor.IsEditorDarkMode = WinoApplication.Current.UnderlyingThemeService.IsUnderlyingThemeDark();

        SignatureNameTextBox.Header = Translator.SignatureEditorDialog_SignatureName_TitleNew;

        // TODO: Should be added additional logic to enable/disable primary button when the editor content changed.
        IsPrimaryButtonEnabled = true;
    }

    public SignatureEditorDialog(AccountSignature signatureModel)
    {
        InitializeComponent();
        SignatureEditor.IsEditorDarkMode = WinoApplication.Current.UnderlyingThemeService.IsUnderlyingThemeDark();

        Result = new AccountSignature
        {
            Id = signatureModel.Id,
            Name = signatureModel.Name,
            MailAccountId = signatureModel.MailAccountId,
            HtmlBody = signatureModel.HtmlBody
        };

        SignatureNameTextBox.Text = Result.Name.Trim();
        SignatureNameTextBox.Header = string.Format(Translator.SignatureEditorDialog_SignatureName_TitleEdit, Result.Name);

        // TODO: Should be added additional logic to enable/disable primary button when the editor content changed.
        IsPrimaryButtonEnabled = true;
    }

    private async void SignatureDialogOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        var preferences = WinoApplication.Current.Services.GetRequiredService<IPreferencesService>();
        var session = SignatureEditor.Session;

        try
        {
            await session.SetDefaultTypographyAsync(preferences.ComposerFont, preferences.ComposerFontSize);
            await session.RenderHtmlAsync(Result?.HtmlBody ?? string.Empty);
        }
        catch (ObjectDisposedException)
        {
            // The dialog closed before the editor started.
        }
        catch (Exception exception)
        {
            Serilog.Log.Error(exception, "The signature editor failed to load the signature.");
        }
    }

    private async void DialogClosed(ContentDialog sender, ContentDialogClosedEventArgs args)
        => await SignatureEditor.Session.DisposeAsync();

    private async void SaveClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // The dialog stays open until the editor has handed over its content.
        var deferral = args.GetDeferral();

        try
        {
            string newSignature;

            try
            {
                newSignature = await SignatureEditor.Session.GetHtmlBodyAsync() ?? string.Empty;
            }
            catch (Exception exception) when (exception is ObjectDisposedException or InvalidOperationException)
            {
                // The editor never showed the signature, so its content would replace the saved one with nothing.
                return;
            }

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
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void CancelClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        Hide();
    }

    private void SignatureNameTextBoxTextChanged(object sender, TextChangedEventArgs e) => IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(SignatureNameTextBox.Text);

    private void EditorLinkOpenRequested(object? sender, Uri uri) => EditorLinkLauncher.Open(uri);
}
