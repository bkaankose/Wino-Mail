using Microsoft.Extensions.DependencyInjection;
using Windows.UI.Xaml.Controls;
using Wino.Core.Domain.Interfaces;


namespace Wino.Mail.Dialogs;

public sealed partial class MessageSourceDialog : ContentDialog
{
    private readonly IClipboardService _clipboardService = App.Current.Services.GetService<IClipboardService>();
    public string MessageSource { get; set; }
    public bool Copied { get; set; }
    public MessageSourceDialog()
    {
        this.InitializeComponent();
    }

    private void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        _clipboardService.CopyClipboardAsync(MessageSource);
        Copied = true;
    }
}
