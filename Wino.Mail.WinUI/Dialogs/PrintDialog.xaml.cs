using System.Collections.Generic;
using System.Drawing.Printing;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using Serilog;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Printing;

namespace Wino.Mail.WinUI.Dialogs;

/// <summary>
/// Custom print dialog for configuring WebView2 print settings.
/// </summary>
public sealed partial class PrintDialog : ContentDialog
{
    public WebView2PrintSettingsModel PrintSettings { get; set; } = new WebView2PrintSettingsModel();

    public PrintDialog()
    {
        this.InitializeComponent();
    }

    /// <summary>
    /// Initializes the dialog with existing print settings.
    /// </summary>
    /// <param name="printSettings">The initial print settings to load.</param>
    public PrintDialog(WebView2PrintSettingsModel printSettings = null)
    {
        if (printSettings != null) PrintSettings = printSettings;

        this.InitializeComponent();
    }

    private void PrintDialog_Loaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => LoadSettingsToUI(PrintSettings);

    private void OrientationRadio_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is RadioButtons radioButtons)
        {
            PrintSettings.Orientation = (PrintOrientation)radioButtons.SelectedIndex;
        }
    }

    private void PrinterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox comboBox && comboBox.SelectedItem != null)
        {
            PrintSettings.PrinterName = comboBox.SelectedItem.ToString();
        }
    }

    /// <summary>
    /// Sets the list of available printers for the dialog.
    /// </summary>
    /// <param name="printers">List of available printer names.</param>
    public void SetAvailablePrinters(IEnumerable<string> printers)
    {
        var printerList = printers?.ToList() ?? new List<string>();

        if (this.FindName("PrinterComboBox") is ComboBox printerComboBox)
        {
            printerComboBox.ItemsSource = printerList;

            if (printerList.Any())
            {
                // Set to first printer or to the one in settings
                var targetPrinter = !string.IsNullOrEmpty(PrintSettings.PrinterName)
                    ? PrintSettings.PrinterName
                    : printerList.First();

                var index = printerList.IndexOf(targetPrinter);
                printerComboBox.SelectedIndex = index >= 0 ? index : 0;

                // Update the settings model with the selected printer
                PrintSettings.PrinterName = printerComboBox.SelectedItem?.ToString() ?? string.Empty;
            }
        }
    }

    /// <summary>
    /// Loads available printers asynchronously and sets them in the dialog.
    /// </summary>
    public async Task LoadAvailablePrintersAsync()
    {
        try
        {
            var printers = await Task.Run(() =>
            {
                var printerList = new List<string>();

                // Get all installed printers using System.Drawing.Printing
                foreach (string printerName in PrinterSettings.InstalledPrinters)
                {
                    printerList.Add(printerName);
                }

                return printerList.AsEnumerable();
            });

            SetAvailablePrinters(printers);
        }
        catch (System.Exception ex)
        {
            // Log the exception if logging is available
            Log.Error(ex, "Error getting available printers");

            // Set empty list if printer discovery fails
            SetAvailablePrinters(Enumerable.Empty<string>());
        }
    }

    private void LoadSettingsToUI(WebView2PrintSettingsModel settings)
    {
        if (settings == null) return;

        // Only handle orientation manually since other properties are bound via x:Bind
        if (this.FindName("OrientationRadioButtons") is RadioButtons orientationRadio)
        {
            orientationRadio.SelectedIndex = (int)settings.Orientation;
        }
    }

    private void UpdateSettingsFromUI()
    {
        // Most properties are bound via x:Bind, only handle orientation manually
        if (this.FindName("OrientationRadioButtons") is RadioButtons orientationRadio)
        {
            PrintSettings.Orientation = (PrintOrientation)orientationRadio.SelectedIndex;
        }

        // Also update printer name from ComboBox since it uses ItemsSource binding
        if (this.FindName("PrinterComboBox") is ComboBox printerComboBox &&
            printerComboBox.SelectedItem != null)
        {
            PrintSettings.PrinterName = printerComboBox.SelectedItem.ToString();
        }
    }

    /// <summary>
    /// Validates the current print settings before closing the dialog.
    /// </summary>
    /// <returns>True if settings are valid, false otherwise.</returns>
    private bool ValidateSettings()
    {
        // Check if a printer is selected
        if (this.FindName("PrinterComboBox") is ComboBox printerComboBox &&
            printerComboBox.SelectedItem == null)
        {
            return false;
        }

        // Copies validation is handled by the bound property with validation in the model
        if (PrintSettings.Copies <= 0)
        {
            return false;
        }

        return true;
    }

    private void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // Update settings from UI before validation
        UpdateSettingsFromUI();

        // Validate settings before closing
        if (!ValidateSettings())
        {
            args.Cancel = true;
        }
    }

    private void ContentDialog_SecondaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // Cancel was clicked, no validation needed
    }
}
