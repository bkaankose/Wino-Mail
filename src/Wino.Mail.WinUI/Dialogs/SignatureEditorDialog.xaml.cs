using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Interfaces;
using Wino.Editor;
using Wino.Helpers;
using Wino.Mail.WinUI;

namespace Wino.Dialogs;

public sealed partial class SignatureEditorDialog : ContentDialog
{
    public AccountSignature Result = null!;

    public SignatureEditorDialog()
    {
        InitializeComponent();
        WebViewEditor.IsEditorDarkMode = WinoApplication.Current.UnderlyingThemeService.IsUnderlyingThemeDark();

        SignatureNameTextBox.Header = Translator.SignatureEditorDialog_SignatureName_TitleNew;

        // TODO: Should be added additional logic to enable/disable primary button when webview content changed.
        IsPrimaryButtonEnabled = true;
    }

    public SignatureEditorDialog(AccountSignature signatureModel)
    {
        InitializeComponent();
        WebViewEditor.IsEditorDarkMode = WinoApplication.Current.UnderlyingThemeService.IsUnderlyingThemeDark();

        Result = new AccountSignature
        {
            Id = signatureModel.Id,
            Name = signatureModel.Name,
            MailAccountId = signatureModel.MailAccountId,
            HtmlBody = signatureModel.HtmlBody
        };

        SignatureNameTextBox.Text = Result.Name.Trim();
        SignatureNameTextBox.Header = string.Format(Translator.SignatureEditorDialog_SignatureName_TitleEdit, Result.Name);

        // TODO: Should be added additional logic to enable/disable primary button when webview content changed.
        IsPrimaryButtonEnabled = true;
    }

    private async void SignatureDialogOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        WebViewEditor.Restarted += WebViewEditor_Restarted;
        await LoadSignatureAsync();
    }

    private async void WebViewEditor_Restarted(object? sender, EventArgs e) => await LoadSignatureAsync();

    private async Task LoadSignatureAsync()
    {
        var preferences = WinoApplication.Current.Services.GetRequiredService<IPreferencesService>();

        try
        {
            await WebViewEditor.SetDefaultTypographyAsync(preferences.ComposerFont, preferences.ComposerFontSize);
            await WebViewEditor.RenderHtmlAsync(Result?.HtmlBody ?? string.Empty);
        }
        catch (ObjectDisposedException)
        {
            // The dialog closed before the editor started.
        }
        catch (Exception exception) when (WinoMailEditor.IsStartupFailure(exception))
        {
            WebViewEditor.ReportInitializationFailure(exception);
        }
    }

    private void DialogClosed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        WebViewEditor.Restarted -= WebViewEditor_Restarted;
        WebViewEditor.Dispose();
    }

    private async void SaveClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        string newSignature;

        try
        {
            newSignature = await WebViewEditor.GetHtmlBodyAsync() ?? string.Empty;
        }
        catch (ObjectDisposedException)
        {
            return;
        }
        catch (Exception exception) when (WinoMailEditor.IsStartupFailure(exception))
        {
            WebViewEditor.ReportInitializationFailure(exception);
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

        Hide();
    }

    private void CancelClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        Hide();
    }

    private void SignatureNameTextBoxTextChanged(object sender, TextChangedEventArgs e) => IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(SignatureNameTextBox.Text);

    private void EditorLinkOpenRequested(object? sender, Uri uri) => EditorLinkLauncher.Open(uri);
}
