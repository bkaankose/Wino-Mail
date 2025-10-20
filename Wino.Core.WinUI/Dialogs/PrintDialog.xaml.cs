using System.Collections.Generic;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain.Models.Printing;
using Wino.Core.WinUI.Models;

namespace Wino.Core.WinUI.Dialogs;

/// <summary>
/// Custom print dialog for configuring WebView2 print settings.
/// </summary>
public sealed partial class PrintDialog : ContentDialog
{
    /// <summary>
    /// The ViewModel that handles the dialog's data binding and logic.
    /// </summary>
    public PrintDialogViewModel ViewModel { get; }

    /// <summary>
    /// Gets the configured print settings from the dialog.
    /// </summary>
    public WebView2PrintSettingsModel PrintSettings => ViewModel.PrintSettings;

    public PrintDialog()
    {
        this.InitializeComponent();
        ViewModel = new PrintDialogViewModel();
        ViewModel.Initialize();
    }

    /// <summary>
    /// Initializes the dialog with existing print settings.
    /// </summary>
    /// <param name="printSettings">The initial print settings to load.</param>
    public PrintDialog(WebView2PrintSettingsModel printSettings)
    {
        this.InitializeComponent();
        ViewModel = new PrintDialogViewModel();
        ViewModel.Initialize(printSettings);
    }

    /// <summary>
    /// Sets the list of available printers for the dialog.
    /// </summary>
    /// <param name="printers">List of available printer names.</param>
    public void SetAvailablePrinters(IEnumerable<string> printers)
    {
        ViewModel.SetAvailablePrinters(printers);
    }

    /// <summary>
    /// Validates the current print settings before closing the dialog.
    /// </summary>
    /// <returns>True if settings are valid, false otherwise.</returns>
    private bool ValidateSettings()
    {
        // Validate printer selection
        if (string.IsNullOrWhiteSpace(PrintSettings.PrinterName))
        {
            // Show error message or set focus to printer selection
            return false;
        }

        // Validate copies
        if (PrintSettings.Copies <= 0)
        {
            return false;
        }

        // Validate page ranges if custom range is specified
        if (ViewModel.IsCustomPageRange && !string.IsNullOrWhiteSpace(PrintSettings.PageRanges))
        {
            // Basic validation for page ranges format
            // More comprehensive validation could be added here
            var pageRanges = PrintSettings.PageRanges.Trim();
            if (string.IsNullOrEmpty(pageRanges))
            {
                return false;
            }
        }

        // Validate margins
        if (PrintSettings.MarginTop < 0 || PrintSettings.MarginBottom < 0 ||
            PrintSettings.MarginLeft < 0 || PrintSettings.MarginRight < 0)
        {
            return false;
        }

        // Validate scale factor
        if (PrintSettings.ScaleFactor < 0.1 || PrintSettings.ScaleFactor > 2.0)
        {
            return false;
        }

        return true;
    }

    private void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // Validate settings before closing
        if (!ValidateSettings())
        {
            args.Cancel = true;
            // Could show error message here
        }
    }

    private void ContentDialog_SecondaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // Cancel was clicked, no validation needed
    }
}