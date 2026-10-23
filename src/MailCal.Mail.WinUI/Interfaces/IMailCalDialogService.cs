using System;
using System.Threading.Tasks;
using MailCal.Core.Domain.Interfaces;
using MailCal.Dialogs;
using MailCal.Editor;

namespace MailCal.Mail.WinUI.Interfaces;

/// <summary>
/// Windows-only dialogs. Every ContentDialog in the WinUI head is created and presented by
/// <see cref="MailCal.Services.DialogService"/>; this interface exposes the ones whose inputs or
/// results are WinUI or editor types that the shared <c>IMailDialogService</c> cannot carry.
/// </summary>
public interface IMailCalDialogService
{
    /// <summary>
    /// Confirmation for an action that is hard to undo. Cancel is the default button, so Enter does not confirm it.
    /// </summary>
    Task<bool> ShowCautiousConfirmationDialogAsync(string title, string message, string confirmButtonText);

    Task<EditorLinkCommandArgs?> ShowEditorLinkDialogAsync(string url, string text, bool isImageSelected);

    Task<EditorImagePropertiesCommandArgs?> ShowEditorImagePropertiesDialogAsync(string altText, string linkUrl);

    Task<EditorTableCommandArgs?> ShowEditorTableDialogAsync();


    /// <summary>
    /// Opens the waiting surface for a browser sign-in on the main window without waiting for it to close.
    /// </summary>
    /// <returns>The open dialog, or null when it could not be shown because another dialog owns the window.</returns>
    ExternalBrowserAuthenticationDialog? OpenExternalBrowserAuthenticationDialog(ExternalBrowserAuthenticationRequest request, Action cancelRequested);
}
