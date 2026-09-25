using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.WinUI;


namespace Wino.Mail.Dialogs;

public sealed partial class MessageSourceDialog : ContentDialog
{
    private readonly INativeAppService? _nativeAppService = App.Current.Services.GetService<INativeAppService>();
    public string MessageSource { get; set; } = string.Empty;
    public bool Copied { get; set; }
    public MessageSourceDialog()
    {
        this.InitializeComponent();
    }

    private void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        _nativeAppService!.CopyClipboardAsync(MessageSource);
        Copied = true;
    }
}
