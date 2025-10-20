using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Drawing.Printing;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Wino.Core.Domain.Models.Printing;
using Wino.Core.WinUI.Dialogs;
using Wino.Core.WinUI.Interfaces;

namespace Wino.Core.WinUI.Services;

/// <summary>
/// Service implementation for displaying the custom print dialog and managing print settings.
/// </summary>
public class PrintDialogService : IPrintDialogService
{
    /// <summary>
    /// Shows the print dialog and returns the configured print settings.
    /// </summary>
    /// <param name="initialSettings">Initial print settings to populate the dialog with. If null, default settings will be used.</param>
    /// <param name="availablePrinters">List of available printers to show in the dialog. If null or empty, the service will discover printers.</param>
    /// <returns>
    /// A task that resolves to the configured WebView2PrintSettingsModel if the user clicked Print,
    /// or null if the user cancelled the dialog.
    /// </returns>
    [UnconditionalSuppressMessage("ReflectionAnalysis", "IL2070:UnrecognizedReflectionPattern", 
        Justification = "GetProperty is used for backward compatibility and gracefully handles failures")]
    public async Task<WebView2PrintSettingsModel> ShowPrintDialogAsync(
        WebView2PrintSettingsModel initialSettings = null,
        IEnumerable<string> availablePrinters = null)
    {
        try
        {
            // Note: XamlRoot will be set by the calling code when showing the dialog

            // Create the print dialog
            var dialog = initialSettings != null 
                ? new PrintDialog(initialSettings) 
                : new PrintDialog();

            // The XamlRoot will be set by the calling code when showing the dialog

            // Get available printers if not provided
            var printers = availablePrinters ?? await GetAvailablePrintersAsync();
            dialog.SetAvailablePrinters(printers);

            // Show the dialog
            var result = await dialog.ShowAsync();

            // Return the settings if user clicked Print, otherwise null
            return result == Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary 
                ? dialog.PrintSettings 
                : null;
        }
        catch (Exception ex)
        {
            // Log the exception if logging is available
            System.Diagnostics.Debug.WriteLine($"Error showing print dialog: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Gets the list of available printers on the system.
    /// </summary>
    /// <returns>A task that resolves to a list of available printer names.</returns>
    public async Task<IEnumerable<string>> GetAvailablePrintersAsync()
    {
        return await Task.Run(() =>
        {
            try
            {
                var printers = new List<string>();
                
                // Get all installed printers using System.Drawing.Printing
                foreach (string printerName in PrinterSettings.InstalledPrinters)
                {
                    printers.Add(printerName);
                }

                return printers.AsEnumerable();
            }
            catch (Exception ex)
            {
                // Log the exception if logging is available
                System.Diagnostics.Debug.WriteLine($"Error getting available printers: {ex.Message}");
                return Enumerable.Empty<string>();
            }
        });
    }
}