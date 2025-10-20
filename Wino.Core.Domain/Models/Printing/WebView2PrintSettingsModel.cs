using System.ComponentModel;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Printing;

/// <summary>
/// Wrapper model for CoreWebView2PrintSettings that provides bindable properties for UI controls.
/// </summary>
public class WebView2PrintSettingsModel : INotifyPropertyChanged
{
    private string _printerName = string.Empty;
    private PrintOrientation _orientation = PrintOrientation.Portrait;
    private PrintColorMode _colorMode = PrintColorMode.Color;
    private PrintCollation _collation = PrintCollation.Default;
    private PrintDuplex _duplex = PrintDuplex.Default;
    private PrintMediaSize _mediaSize = PrintMediaSize.Default;
    private int _copies = 1;
    private double _marginTop = 1.0;
    private double _marginBottom = 1.0;
    private double _marginLeft = 1.0;
    private double _marginRight = 1.0;
    private bool _shouldPrintBackgrounds = false;
    private bool _shouldPrintSelectionOnly = false;
    private bool _shouldPrintHeaderAndFooter = true;
    private string _headerTitle = string.Empty;
    private string _footerUri = string.Empty;
    private double _scaleFactor = 1.0;
    private int _pagesPerSide = 1;
    private string _pageRanges = string.Empty;

    public event PropertyChangedEventHandler PropertyChanged;

    /// <summary>
    /// Name of the printer to use for printing.
    /// </summary>
    public string PrinterName
    {
        get => _printerName;
        set
        {
            if (_printerName != value)
            {
                _printerName = value;
                OnPropertyChanged(nameof(PrinterName));
            }
        }
    }

    /// <summary>
    /// Orientation of the printed document.
    /// </summary>
    public PrintOrientation Orientation
    {
        get => _orientation;
        set
        {
            if (_orientation != value)
            {
                _orientation = value;
                OnPropertyChanged(nameof(Orientation));
            }
        }
    }

    /// <summary>
    /// Color mode for printing.
    /// </summary>
    public PrintColorMode ColorMode
    {
        get => _colorMode;
        set
        {
            if (_colorMode != value)
            {
                _colorMode = value;
                OnPropertyChanged(nameof(ColorMode));
            }
        }
    }

    /// <summary>
    /// Collation setting for multiple copies.
    /// </summary>
    public PrintCollation Collation
    {
        get => _collation;
        set
        {
            if (_collation != value)
            {
                _collation = value;
                OnPropertyChanged(nameof(Collation));
            }
        }
    }

    /// <summary>
    /// Duplex printing mode.
    /// </summary>
    public PrintDuplex Duplex
    {
        get => _duplex;
        set
        {
            if (_duplex != value)
            {
                _duplex = value;
                OnPropertyChanged(nameof(Duplex));
            }
        }
    }

    /// <summary>
    /// Media size for printing.
    /// </summary>
    public PrintMediaSize MediaSize
    {
        get => _mediaSize;
        set
        {
            if (_mediaSize != value)
            {
                _mediaSize = value;
                OnPropertyChanged(nameof(MediaSize));
            }
        }
    }

    /// <summary>
    /// Number of copies to print.
    /// </summary>
    public int Copies
    {
        get => _copies;
        set
        {
            if (_copies != value && value > 0)
            {
                _copies = value;
                OnPropertyChanged(nameof(Copies));
            }
        }
    }

    /// <summary>
    /// Top margin in inches.
    /// </summary>
    public double MarginTop
    {
        get => _marginTop;
        set
        {
            if (_marginTop != value && value >= 0)
            {
                _marginTop = value;
                OnPropertyChanged(nameof(MarginTop));
            }
        }
    }

    /// <summary>
    /// Bottom margin in inches.
    /// </summary>
    public double MarginBottom
    {
        get => _marginBottom;
        set
        {
            if (_marginBottom != value && value >= 0)
            {
                _marginBottom = value;
                OnPropertyChanged(nameof(MarginBottom));
            }
        }
    }

    /// <summary>
    /// Left margin in inches.
    /// </summary>
    public double MarginLeft
    {
        get => _marginLeft;
        set
        {
            if (_marginLeft != value && value >= 0)
            {
                _marginLeft = value;
                OnPropertyChanged(nameof(MarginLeft));
            }
        }
    }

    /// <summary>
    /// Right margin in inches.
    /// </summary>
    public double MarginRight
    {
        get => _marginRight;
        set
        {
            if (_marginRight != value && value >= 0)
            {
                _marginRight = value;
                OnPropertyChanged(nameof(MarginRight));
            }
        }
    }

    /// <summary>
    /// Whether to print background colors and images.
    /// </summary>
    public bool ShouldPrintBackgrounds
    {
        get => _shouldPrintBackgrounds;
        set
        {
            if (_shouldPrintBackgrounds != value)
            {
                _shouldPrintBackgrounds = value;
                OnPropertyChanged(nameof(ShouldPrintBackgrounds));
            }
        }
    }

    /// <summary>
    /// Whether to print only the selected content.
    /// </summary>
    public bool ShouldPrintSelectionOnly
    {
        get => _shouldPrintSelectionOnly;
        set
        {
            if (_shouldPrintSelectionOnly != value)
            {
                _shouldPrintSelectionOnly = value;
                OnPropertyChanged(nameof(ShouldPrintSelectionOnly));
            }
        }
    }

    /// <summary>
    /// Whether to print header and footer.
    /// </summary>
    public bool ShouldPrintHeaderAndFooter
    {
        get => _shouldPrintHeaderAndFooter;
        set
        {
            if (_shouldPrintHeaderAndFooter != value)
            {
                _shouldPrintHeaderAndFooter = value;
                OnPropertyChanged(nameof(ShouldPrintHeaderAndFooter));
            }
        }
    }

    /// <summary>
    /// Title to display in the header.
    /// </summary>
    public string HeaderTitle
    {
        get => _headerTitle;
        set
        {
            if (_headerTitle != value)
            {
                _headerTitle = value ?? string.Empty;
                OnPropertyChanged(nameof(HeaderTitle));
            }
        }
    }

    /// <summary>
    /// URI to display in the footer.
    /// </summary>
    public string FooterUri
    {
        get => _footerUri;
        set
        {
            if (_footerUri != value)
            {
                _footerUri = value ?? string.Empty;
                OnPropertyChanged(nameof(FooterUri));
            }
        }
    }

    /// <summary>
    /// Scale factor for printing (0.1 to 2.0).
    /// </summary>
    public double ScaleFactor
    {
        get => _scaleFactor;
        set
        {
            if (_scaleFactor != value && value >= 0.1 && value <= 2.0)
            {
                _scaleFactor = value;
                OnPropertyChanged(nameof(ScaleFactor));
            }
        }
    }

    /// <summary>
    /// Number of pages to print per sheet (1, 2, 4, 6, 9, 16).
    /// </summary>
    public int PagesPerSide
    {
        get => _pagesPerSide;
        set
        {
            var validValues = new[] { 1, 2, 4, 6, 9, 16 };
            if (_pagesPerSide != value && System.Array.IndexOf(validValues, value) >= 0)
            {
                _pagesPerSide = value;
                OnPropertyChanged(nameof(PagesPerSide));
            }
        }
    }

    /// <summary>
    /// Page ranges to print (e.g., "1-3,5,7-9").
    /// </summary>
    public string PageRanges
    {
        get => _pageRanges;
        set
        {
            if (_pageRanges != value)
            {
                _pageRanges = value ?? string.Empty;
                OnPropertyChanged(nameof(PageRanges));
            }
        }
    }



    protected virtual void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}