using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.WinUI;

namespace Wino.Mail.Dialogs;

public sealed partial class ImapValidationFailedDialog : ContentDialog
{
    private readonly INativeAppService _nativeAppService = App.Current.Services.GetRequiredService<INativeAppService>();

    public string ErrorMessage { get; set; } = string.Empty;
    public string ProtocolLog { get; set; } = string.Empty;
    public bool Copied { get; private set; }

    public ImapValidationFailedDialog()
    {
        InitializeComponent();
    }

    private async void CopyDiagnosticsClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        await _nativeAppService.CopyClipboardAsync(
            $"{ErrorMessage}{Environment.NewLine}{Environment.NewLine}{ProtocolLog}");
        Copied = true;
    }
}
