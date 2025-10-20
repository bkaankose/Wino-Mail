using System.Collections.Generic;
using System.Threading.Tasks;
using Wino.Core.Domain.Models.Printing;

namespace Wino.Core.WinUI.Interfaces;

/// <summary>
/// Service interface for displaying the custom print dialog and managing print settings.
/// </summary>
public interface IPrintDialogService
{
    /// <summary>
    /// Shows the print dialog and returns the configured print settings.
    /// </summary>
    /// <param name="initialSettings">Initial print settings to populate the dialog with. If null, default settings will be used.</param>
    /// <param name="availablePrinters">List of available printers to show in the dialog. If null or empty, the service should attempt to discover printers.</param>
    /// <returns>
    /// A task that resolves to the configured WebView2PrintSettingsModel if the user clicked Print,
    /// or null if the user cancelled the dialog.
    /// </returns>
    Task<WebView2PrintSettingsModel> ShowPrintDialogAsync(
        WebView2PrintSettingsModel initialSettings = null,
        IEnumerable<string> availablePrinters = null);

    /// <summary>
    /// Gets the list of available printers on the system.
    /// </summary>
    /// <returns>A task that resolves to a list of available printer names.</returns>
    Task<IEnumerable<string>> GetAvailablePrintersAsync();
}