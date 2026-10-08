using Wino.Mail.WinUI.Interfaces;
using System;
using CommunityToolkit.WinUI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.WinUI;

namespace Wino.Dialogs;

/// <summary>
/// Waiting surface for an OAuth sign-in that runs in the external browser. It offers the
/// authorization address for manual copy when the browser never opens and lets the user cancel.
/// </summary>
public sealed partial class ExternalBrowserAuthenticationDialog : ContentDialog
{
    private readonly INativeAppService _nativeAppService = App.Current.Services.GetRequiredService<INativeAppService>();
    private bool _isCompleted;

    /// <summary>
    /// Raised once when the user dismisses the dialog before the sign-in completed.
    /// </summary>
    public event EventHandler? CancelRequested;

    [GeneratedDependencyProperty(DefaultValue = "")]
    public partial string Message { get; set; }

    [GeneratedDependencyProperty]
    public partial Uri? AuthorizationUri { get; set; }

    [GeneratedDependencyProperty(DefaultValue = false)]
    public partial bool IsBrowserLaunchFailed { get; set; }

    [GeneratedDependencyProperty(DefaultValue = false)]
    public partial bool IsLinkCopied { get; set; }

    public ExternalBrowserAuthenticationDialog()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Closes the dialog because the authorization flow finished on its own.
    /// </summary>
    public void Complete()
    {
        _isCompleted = true;

        Hide();
    }

    private void DialogClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        // Close button and Escape both land here; anything but a programmatic completion is a cancel.
        if (_isCompleted) return;

        _isCompleted = true;
        CancelRequested?.Invoke(this, EventArgs.Empty);
    }

    private async void CopyLinkClicked(object sender, RoutedEventArgs e)
    {
        if (AuthorizationUri is null) return;

        await _nativeAppService.CopyClipboardAsync(AuthorizationUri.AbsoluteUri);

        IsLinkCopied = true;
    }
}
