using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Printing;

namespace Wino.Core.WinUI.Dialogs;

/// <summary>
/// ViewModel for the PrintDialog that handles data binding and state management.
/// </summary>
public class PrintDialogViewModel : INotifyPropertyChanged
{
    private List<string> _availablePrinters = new();
    private bool _isCustomPageRange = false;
    private WebView2PrintSettingsModel _printSettings = new();

    public event PropertyChangedEventHandler PropertyChanged;

    public PrintDialogViewModel()
    {
        // Initialize default values
        PrintSettings.PropertyChanged += OnPrintSettingsChanged;
    }

    /// <summary>
    /// The print settings model that will be configured by the dialog.
    /// </summary>
    public WebView2PrintSettingsModel PrintSettings
    {
        get => _printSettings;
        set
        {
            if (_printSettings != value)
            {
                if (_printSettings != null)
                    _printSettings.PropertyChanged -= OnPrintSettingsChanged;
                
                _printSettings = value;
                
                if (_printSettings != null)
                    _printSettings.PropertyChanged += OnPrintSettingsChanged;
                
                OnPropertyChanged(nameof(PrintSettings));
                UpdateDerivedProperties();
            }
        }
    }

    /// <summary>
    /// List of available printers.
    /// </summary>
    public List<string> AvailablePrinters
    {
        get => _availablePrinters;
        set
        {
            if (_availablePrinters != value)
            {
                _availablePrinters = value ?? new List<string>();
                OnPropertyChanged(nameof(AvailablePrinters));
            }
        }
    }

    /// <summary>
    /// Index for the orientation radio buttons.
    /// </summary>
    public int OrientationIndex
    {
        get => (int)PrintSettings.Orientation;
        set
        {
            if (value >= 0 && value <= 1)
            {
                PrintSettings.Orientation = (PrintOrientation)value;
                OnPropertyChanged(nameof(OrientationIndex));
            }
        }
    }

    /// <summary>
    /// Index for the color mode radio buttons.
    /// </summary>
    public int ColorModeIndex
    {
        get => (int)PrintSettings.ColorMode;
        set
        {
            if (value >= 0 && value <= 2)
            {
                PrintSettings.ColorMode = (PrintColorMode)value;
                OnPropertyChanged(nameof(ColorModeIndex));
            }
        }
    }

    /// <summary>
    /// Index for the collation radio buttons.
    /// </summary>
    public int CollationIndex
    {
        get => (int)PrintSettings.Collation;
        set
        {
            if (value >= 0 && value <= 2)
            {
                PrintSettings.Collation = (PrintCollation)value;
                OnPropertyChanged(nameof(CollationIndex));
            }
        }
    }

    /// <summary>
    /// Index for the duplex radio buttons.
    /// </summary>
    public int DuplexIndex
    {
        get => (int)PrintSettings.Duplex;
        set
        {
            if (value >= 0 && value <= 3)
            {
                PrintSettings.Duplex = (PrintDuplex)value;
                OnPropertyChanged(nameof(DuplexIndex));
            }
        }
    }

    /// <summary>
    /// Index for the media size combo box.
    /// </summary>
    public int MediaSizeIndex
    {
        get => (int)PrintSettings.MediaSize;
        set
        {
            if (value >= 0 && value <= 9)
            {
                PrintSettings.MediaSize = (PrintMediaSize)value;
                OnPropertyChanged(nameof(MediaSizeIndex));
            }
        }
    }

    /// <summary>
    /// Index for the pages per side combo box.
    /// </summary>
    public int PagesPerSideIndex
    {
        get
        {
            var validValues = new[] { 1, 2, 4, 6, 9, 16 };
            return Array.IndexOf(validValues, PrintSettings.PagesPerSide);
        }
        set
        {
            var validValues = new[] { 1, 2, 4, 6, 9, 16 };
            if (value >= 0 && value < validValues.Length)
            {
                PrintSettings.PagesPerSide = validValues[value];
                OnPropertyChanged(nameof(PagesPerSideIndex));
            }
        }
    }

    /// <summary>
    /// Index for the page range option (0 = All pages, 1 = Custom range).
    /// </summary>
    public int PageRangeOptionIndex
    {
        get => IsCustomPageRange ? 1 : 0;
        set
        {
            IsCustomPageRange = value == 1;
            if (!IsCustomPageRange)
            {
                PrintSettings.PageRanges = string.Empty;
            }
            OnPropertyChanged(nameof(PageRangeOptionIndex));
        }
    }

    /// <summary>
    /// Whether custom page range is selected.
    /// </summary>
    public bool IsCustomPageRange
    {
        get => _isCustomPageRange;
        private set
        {
            if (_isCustomPageRange != value)
            {
                _isCustomPageRange = value;
                OnPropertyChanged(nameof(IsCustomPageRange));
            }
        }
    }

    /// <summary>
    /// Scale factor as percentage text for display.
    /// </summary>
    public string ScalePercentageText => $"{(int)(PrintSettings.ScaleFactor * 100)}%";

    /// <summary>
    /// Initializes the dialog with the provided print settings.
    /// </summary>
    /// <param name="printSettings">The initial print settings.</param>
    public void Initialize(WebView2PrintSettingsModel printSettings = null)
    {
        if (printSettings != null)
        {
            PrintSettings = printSettings;
        }
        else
        {
            PrintSettings = new WebView2PrintSettingsModel();
        }

        UpdateDerivedProperties();
    }

    /// <summary>
    /// Sets the list of available printers.
    /// </summary>
    /// <param name="printers">List of printer names.</param>
    public void SetAvailablePrinters(IEnumerable<string> printers)
    {
        AvailablePrinters = printers?.ToList() ?? new List<string>();
        
        // If current printer is not in the list, select the first one
        if (AvailablePrinters.Any() && !AvailablePrinters.Contains(PrintSettings.PrinterName))
        {
            PrintSettings.PrinterName = AvailablePrinters.First();
        }
    }

    private void OnPrintSettingsChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WebView2PrintSettingsModel.ScaleFactor))
        {
            OnPropertyChanged(nameof(ScalePercentageText));
        }
        else if (e.PropertyName == nameof(WebView2PrintSettingsModel.PageRanges))
        {
            // Update custom page range flag based on whether page ranges is empty
            if (!string.IsNullOrWhiteSpace(PrintSettings.PageRanges))
            {
                IsCustomPageRange = true;
                OnPropertyChanged(nameof(PageRangeOptionIndex));
            }
        }
    }

    private void UpdateDerivedProperties()
    {
        OnPropertyChanged(nameof(OrientationIndex));
        OnPropertyChanged(nameof(ColorModeIndex));
        OnPropertyChanged(nameof(CollationIndex));
        OnPropertyChanged(nameof(DuplexIndex));
        OnPropertyChanged(nameof(MediaSizeIndex));
        OnPropertyChanged(nameof(PagesPerSideIndex));
        OnPropertyChanged(nameof(PageRangeOptionIndex));
        OnPropertyChanged(nameof(ScalePercentageText));
        
        // Update custom page range based on current page ranges value
        IsCustomPageRange = !string.IsNullOrWhiteSpace(PrintSettings.PageRanges);
    }

    protected virtual void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}