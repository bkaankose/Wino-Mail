using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Printing;
using Windows.Data.Pdf;
using Windows.Graphics.Printing;
using Windows.Graphics.Printing.OptionDetails;
using WinRT.Interop;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Printing;
using DomainPrintCollation = Wino.Core.Domain.Enums.PrintCollation;
using DomainPrintDuplex = Wino.Core.Domain.Enums.PrintDuplex;
using DomainPrintMediaSize = Wino.Core.Domain.Enums.PrintMediaSize;
using DomainPrintOrientation = Wino.Core.Domain.Enums.PrintOrientation;

namespace Wino.Mail.WinUI.Services;

/// <summary>
/// Printer service that uses the WinRT print preview UI with a WebView2-backed PDF render callback.
/// </summary>
public class PrintService : IPrintService
{
    private const float PdfRenderDpi = 300f;
    private const float DefaultDpi = 96f;

    private TaskCompletionSource<PrintingResult>? _taskCompletionSource;
    private CanvasPrintDocument? _printDocument;
    private PrintTask? _printTask;
    private PrintTaskOptionDetails? _printTaskOptionDetails;
    private PrintManager? _printManager;
    private PdfDocument? _pdfDocument;
    private Func<WebView2PrintSettingsModel, Task<Stream>>? _renderPdfStreamAsync;
    private WebView2PrintSettingsModel _currentRenderSettings = new();
    private string _printTitle = string.Empty;

    private readonly List<CanvasBitmap> _bitmaps = new();
    private readonly List<int> _pageIndexesToPrint = new();
    private Vector2 _pageSize;
    private Windows.Foundation.Rect _imageableRect;
    private int _pagesPerSheet = 1;
    private int _columns = 1;
    private int _rows = 1;
    private int _sheetCount;

    public async Task<PrintingResult> PrintAsync(nint windowHandle, string printTitle, Func<WebView2PrintSettingsModel, Task<Stream>> renderPdfStreamAsync)
    {
        if (windowHandle == IntPtr.Zero)
            return PrintingResult.Failed;

        if (_taskCompletionSource != null)
        {
            _taskCompletionSource.TrySetResult(PrintingResult.Abandoned);
            CleanupPrintSession();
        }

        _taskCompletionSource = new TaskCompletionSource<PrintingResult>();
        _renderPdfStreamAsync = renderPdfStreamAsync ?? throw new ArgumentNullException(nameof(renderPdfStreamAsync));
        _printTitle = printTitle ?? throw new ArgumentNullException(nameof(printTitle));
        _currentRenderSettings = new WebView2PrintSettingsModel();

        _printDocument = new CanvasPrintDocument();
        _printDocument.PrintTaskOptionsChanged += OnDocumentTaskOptionsChanged;
        _printDocument.Preview += OnDocumentPreview;
        _printDocument.Print += OnDocumentPrint;

        _printManager = PrintManagerInterop.GetForWindow(windowHandle);
        _printManager.PrintTaskRequested += OnPrintTaskRequested;

        try
        {
            await ReloadPdfDocumentAsync(_currentRenderSettings);
            await PrintManagerInterop.ShowPrintUIForWindowAsync(windowHandle);
            return await _taskCompletionSource.Task;
        }
        finally
        {
            CleanupPrintSession();
        }
    }

    private void CleanupPrintSession()
    {
        var printManager = _printManager;
        _printManager = null;
        if (printManager != null)
        {
            try
            {
                printManager.PrintTaskRequested -= OnPrintTaskRequested;
            }
            catch (ObjectDisposedException)
            {
            }
        }

        var printTaskOptionDetails = _printTaskOptionDetails;
        _printTaskOptionDetails = null;
        if (printTaskOptionDetails != null)
        {
            try
            {
                printTaskOptionDetails.OptionChanged -= OnPrintTaskOptionChanged;
            }
            catch (ObjectDisposedException)
            {
            }
        }

        var printTask = _printTask;
        _printTask = null;
        if (printTask != null)
        {
            try
            {
                printTask.Completed -= OnPrintTaskCompleted;
            }
            catch (ObjectDisposedException)
            {
            }
        }

        var printDocument = _printDocument;
        _printDocument = null;
        if (printDocument != null)
        {
            try
            {
                printDocument.PrintTaskOptionsChanged -= OnDocumentTaskOptionsChanged;
            }
            catch (ObjectDisposedException)
            {
            }

            try
            {
                printDocument.Preview -= OnDocumentPreview;
            }
            catch (ObjectDisposedException)
            {
            }

            try
            {
                printDocument.Print -= OnDocumentPrint;
            }
            catch (ObjectDisposedException)
            {
            }
        }

        _pdfDocument = null;
        ClearBitmaps();
        _pageIndexesToPrint.Clear();
        _taskCompletionSource = null;
        _renderPdfStreamAsync = null;
        _printTitle = string.Empty;
    }

    private void ClearBitmaps()
    {
        foreach (var bitmap in _bitmaps)
        {
            bitmap.Dispose();
        }

        _bitmaps.Clear();
    }

    private void OnPrintTaskRequested(PrintManager sender, PrintTaskRequestedEventArgs args)
    {
        _printTask = args.Request.CreatePrintTask(_printTitle, createPrintTaskArgs =>
        {
            if (_printDocument == null)
                return;

            createPrintTaskArgs.SetSource(_printDocument);
        });

        _printTask.Completed += OnPrintTaskCompleted;

        _printTaskOptionDetails = PrintTaskOptionDetails.GetFromPrintTaskOptions(_printTask.Options);
        _printTaskOptionDetails.DisplayedOptions.Clear();
        TryAddDisplayedOption(StandardPrintTaskOptions.Copies);
        TryAddDisplayedOption(StandardPrintTaskOptions.Orientation);
        TryAddDisplayedOption(StandardPrintTaskOptions.MediaSize);
        TryAddDisplayedOption(StandardPrintTaskOptions.Collation);
        TryAddDisplayedOption(StandardPrintTaskOptions.Duplex);
        TryAddDisplayedOption(StandardPrintTaskOptions.CustomPageRanges);
        TryAddDisplayedOption(StandardPrintTaskOptions.NUp);
        _printTaskOptionDetails.OptionChanged += OnPrintTaskOptionChanged;
    }

    private void OnPrintTaskCompleted(PrintTask sender, PrintTaskCompletedEventArgs args)
        => _taskCompletionSource?.TrySetResult(args.Completion switch
        {
            PrintTaskCompletion.Submitted => PrintingResult.Submitted,
            PrintTaskCompletion.Canceled => PrintingResult.Canceled,
            PrintTaskCompletion.Failed => PrintingResult.Failed,
            _ => PrintingResult.Abandoned
        });

    private void OnPrintTaskOptionChanged(PrintTaskOptionDetails sender, PrintTaskOptionChangedEventArgs args)
        => _printDocument?.InvalidatePreview();

    private async void OnDocumentTaskOptionsChanged(CanvasPrintDocument sender, CanvasPrintTaskOptionsChangedEventArgs args)
    {
        var deferral = args.GetDeferral();

        try
        {
            var newSettings = CreateRenderSettings(args.PrintTaskOptions);

            if (ShouldReloadPdf(newSettings))
            {
                await ReloadPdfDocumentAsync(newSettings);
            }
            else
            {
                _currentRenderSettings = newSettings;
            }

            UpdatePreviewLayout(args.PrintTaskOptions);
            sender.InvalidatePreview();
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async Task ReloadPdfDocumentAsync(WebView2PrintSettingsModel settings)
    {
        if (_renderPdfStreamAsync == null)
            throw new InvalidOperationException("No PDF render callback is registered.");

        await using var pdfStream = await _renderPdfStreamAsync(settings);
        var randomAccessStream = pdfStream.AsRandomAccessStream();

        _pdfDocument = await PdfDocument.LoadFromStreamAsync(randomAccessStream);
        _currentRenderSettings = settings;

        ClearBitmaps();

        if (_printDocument == null || _pdfDocument == null)
            return;

        for (var i = 0; i < _pdfDocument.PageCount; i++)
        {
            using var page = _pdfDocument.GetPage((uint)i);
            using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            var renderOptions = CreateRenderOptions(page);
            await page.RenderToStreamAsync(stream, renderOptions);
            stream.Seek(0);
            var bitmap = await CanvasBitmap.LoadAsync(_printDocument, stream);
            _bitmaps.Add(bitmap);
        }
    }

    private static PdfPageRenderOptions CreateRenderOptions(PdfPage page)
    {
        var scale = PdfRenderDpi / DefaultDpi;
        var destinationWidth = Math.Max(1u, (uint)Math.Ceiling(page.Size.Width * scale));
        var destinationHeight = Math.Max(1u, (uint)Math.Ceiling(page.Size.Height * scale));

        return new PdfPageRenderOptions
        {
            DestinationWidth = destinationWidth,
            DestinationHeight = destinationHeight
        };
    }

    private void UpdatePreviewLayout(PrintTaskOptions printTaskOptions)
    {
        if (_pdfDocument == null)
        {
            _sheetCount = 0;
            return;
        }

        var pageDescription = printTaskOptions.GetPageDescription(1);
        _pageSize = pageDescription.PageSize.ToVector2();
        _imageableRect = pageDescription.ImageableRect;

        _pagesPerSheet = GetPagesPerSheet();
        (_columns, _rows) = GetGrid(_pagesPerSheet);

        _pageIndexesToPrint.Clear();
        _pageIndexesToPrint.AddRange(GetPageIndexesToPrint((int)_pdfDocument.PageCount));
        _sheetCount = Math.Max(1, (int)Math.Ceiling(_pageIndexesToPrint.Count / (double)_pagesPerSheet));

        _printDocument?.SetPageCount((uint)_sheetCount);
    }

    private void OnDocumentPreview(CanvasPrintDocument sender, CanvasPreviewEventArgs args)
        => DrawSheet(args.DrawingSession, args.PageNumber);

    private void OnDocumentPrint(CanvasPrintDocument sender, CanvasPrintEventArgs args)
    {
        if (_pdfDocument == null || _sheetCount == 0)
            return;

        for (uint i = 1; i <= _sheetCount; i++)
        {
            using var drawingSession = args.CreateDrawingSession();
            DrawSheet(drawingSession, i);
        }
    }

    private void DrawSheet(CanvasDrawingSession drawingSession, uint pageNumber)
    {
        if (_bitmaps.Count == 0 || _pageIndexesToPrint.Count == 0)
            return;

        var printableSize = new Vector2((float)_imageableRect.Width, (float)_imageableRect.Height);
        var cellWidth = printableSize.X / _columns;
        var cellHeight = printableSize.Y / _rows;
        var topLeft = new Vector2((float)_imageableRect.X, (float)_imageableRect.Y);
        var pageIndex = ((int)pageNumber - 1) * _pagesPerSheet;

        for (var row = 0; row < _rows; row++)
        {
            for (var column = 0; column < _columns; column++)
            {
                if (pageIndex >= _pageIndexesToPrint.Count)
                    return;

                var bitmap = _bitmaps[_pageIndexesToPrint[pageIndex]];
                var cellTopLeft = topLeft + new Vector2(cellWidth * column, cellHeight * row);
                DrawBitmapInCell(drawingSession, bitmap, cellTopLeft, new Vector2(cellWidth, cellHeight));
                pageIndex++;
            }
        }
    }

    private static void DrawBitmapInCell(CanvasDrawingSession drawingSession, CanvasBitmap bitmap, Vector2 cellTopLeft, Vector2 cellSize)
    {
        var bitmapSize = bitmap.Size.ToVector2();
        var scale = Math.Min(cellSize.X / bitmapSize.X, cellSize.Y / bitmapSize.Y);
        var targetSize = bitmapSize * scale;
        var targetOffset = cellTopLeft + (cellSize - targetSize) / 2;
        drawingSession.DrawImage(bitmap, new Windows.Foundation.Rect(targetOffset.X, targetOffset.Y, targetSize.X, targetSize.Y));
    }

    private WebView2PrintSettingsModel CreateRenderSettings(PrintTaskOptions printTaskOptions)
        => new()
        {
            Orientation = GetOrientation(printTaskOptions),
            MediaSize = GetMediaSize(),
            PageRanges = GetPageRanges(),
            MarginTop = _currentRenderSettings.MarginTop,
            MarginBottom = _currentRenderSettings.MarginBottom,
            MarginLeft = _currentRenderSettings.MarginLeft,
            MarginRight = _currentRenderSettings.MarginRight,
            ShouldPrintBackgrounds = _currentRenderSettings.ShouldPrintBackgrounds,
            ShouldPrintSelectionOnly = _currentRenderSettings.ShouldPrintSelectionOnly,
            ShouldPrintHeaderAndFooter = _currentRenderSettings.ShouldPrintHeaderAndFooter,
            HeaderTitle = _currentRenderSettings.HeaderTitle,
            FooterUri = _currentRenderSettings.FooterUri,
            ScaleFactor = _currentRenderSettings.ScaleFactor
        };

    private bool ShouldReloadPdf(WebView2PrintSettingsModel newSettings)
        => newSettings.Orientation != _currentRenderSettings.Orientation
           || newSettings.MediaSize != _currentRenderSettings.MediaSize
           || newSettings.ShouldPrintBackgrounds != _currentRenderSettings.ShouldPrintBackgrounds
           || newSettings.ShouldPrintHeaderAndFooter != _currentRenderSettings.ShouldPrintHeaderAndFooter
           || !string.Equals(newSettings.HeaderTitle, _currentRenderSettings.HeaderTitle, StringComparison.Ordinal)
           || !string.Equals(newSettings.FooterUri, _currentRenderSettings.FooterUri, StringComparison.Ordinal);

    private int GetCopies()
        => GetOptionValue(StandardPrintTaskOptions.Copies) as int? ?? 1;

    private DomainPrintOrientation GetOrientation(PrintTaskOptions printTaskOptions)
    {
        var optionValue = GetOptionValue(StandardPrintTaskOptions.Orientation);
        if (optionValue is DomainPrintOrientation orientation)
            return orientation;

        if (optionValue is string orientationString
            && Enum.TryParse<DomainPrintOrientation>(orientationString, true, out var parsedOrientation))
        {
            return parsedOrientation;
        }

        var pageDescription = printTaskOptions.GetPageDescription(1);
        return pageDescription.PageSize.Width >= pageDescription.PageSize.Height
            ? DomainPrintOrientation.Landscape
            : DomainPrintOrientation.Portrait;
    }

    private DomainPrintMediaSize GetMediaSize()
    {
        var optionValue = GetOptionValue(StandardPrintTaskOptions.MediaSize);
        if (optionValue is DomainPrintMediaSize mediaSize)
            return mediaSize;

        if (optionValue is string mediaSizeString
            && Enum.TryParse<DomainPrintMediaSize>(mediaSizeString, true, out var parsedMediaSize))
        {
            return parsedMediaSize;
        }

        return DomainPrintMediaSize.Default;
    }

    private DomainPrintCollation GetCollation()
    {
        var optionValue = GetOptionValue(StandardPrintTaskOptions.Collation);
        if (optionValue is DomainPrintCollation collation)
            return collation;

        if (optionValue is string collationString
            && Enum.TryParse<DomainPrintCollation>(collationString, true, out var parsedCollation))
        {
            return parsedCollation;
        }

        return DomainPrintCollation.Default;
    }

    private DomainPrintDuplex GetDuplex()
    {
        var optionValue = GetOptionValue(StandardPrintTaskOptions.Duplex);
        if (optionValue is DomainPrintDuplex duplex)
            return duplex;

        if (optionValue is string duplexString)
        {
            return duplexString switch
            {
                nameof(DomainPrintDuplex.Simplex) => DomainPrintDuplex.Simplex,
                nameof(DomainPrintDuplex.DuplexShortEdge) => DomainPrintDuplex.DuplexShortEdge,
                nameof(DomainPrintDuplex.DuplexLongEdge) => DomainPrintDuplex.DuplexLongEdge,
                _ => DomainPrintDuplex.Default
            };
        }

        return DomainPrintDuplex.Default;
    }

    private int GetPagesPerSheet()
    {
        var optionValue = GetOptionValue(StandardPrintTaskOptions.NUp);
        if (optionValue is int pagesPerSheet)
            return NormalizePagesPerSheet(pagesPerSheet);

        if (optionValue is string valueString)
        {
            if (int.TryParse(valueString, out var parsedPagesPerSheet))
                return NormalizePagesPerSheet(parsedPagesPerSheet);

            return valueString switch
            {
                "TwoUp" => 2,
                "FourUp" => 4,
                "SixUp" => 6,
                "NineUp" => 9,
                "SixteenUp" => 16,
                _ => 1
            };
        }

        return 1;
    }

    private string GetPageRanges()
    {
        var optionValue = GetOptionValue(StandardPrintTaskOptions.CustomPageRanges);
        return optionValue?.ToString() ?? string.Empty;
    }

    private object? GetOptionValue(string optionId)
    {
        if (_printTaskOptionDetails == null)
            return null;

        if (!TryGetOption(optionId, out var option))
            return null;

        try
        {
            return option switch
            {
                PrintCopiesOptionDetails copies => copies.Value,
                PrintPageRangeOptionDetails pageRanges => pageRanges.Value,
                IPrintItemListOptionDetails itemList => itemList.Value,
                IPrintNumberOptionDetails number => number.Value,
                _ => null
            };
        }
        catch (COMException)
        {
            return null;
        }
    }

    private void TryAddDisplayedOption(string optionId)
    {
        if (_printTaskOptionDetails == null)
            return;

        if (TryGetOption(optionId, out _))
        {
            _printTaskOptionDetails.DisplayedOptions.Add(optionId);
        }
    }

    private bool TryGetOption(string optionId, out IPrintOptionDetails? option)
    {
        option = null;

        if (_printTaskOptionDetails == null)
            return false;

        try
        {
            if (_printTaskOptionDetails.Options.TryGetValue(optionId, out option))
                return option != null;
        }
        catch (COMException)
        {
            return false;
        }
        catch (KeyNotFoundException)
        {
            return false;
        }

        return false;
    }

    private IEnumerable<int> GetPageIndexesToPrint(int totalPageCount)
    {
        if (string.IsNullOrWhiteSpace(_currentRenderSettings.PageRanges))
            return GetAllPages(totalPageCount);

        var pageIndexes = new List<int>();
        var tokens = _currentRenderSettings.PageRanges.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var token in tokens)
        {
            if (token.Contains('-'))
            {
                var bounds = token.Split('-', StringSplitOptions.TrimEntries);
                if (bounds.Length != 2
                    || !int.TryParse(bounds[0], out var start)
                    || !int.TryParse(bounds[1], out var end))
                {
                    continue;
                }

                if (end < start)
                {
                    (start, end) = (end, start);
                }

                for (var i = start; i <= end; i++)
                {
                    AddPageIndex(pageIndexes, i, totalPageCount);
                }
            }
            else if (int.TryParse(token, out var pageNumber))
            {
                AddPageIndex(pageIndexes, pageNumber, totalPageCount);
            }
        }

        return pageIndexes.Count > 0
            ? pageIndexes
            : GetAllPages(totalPageCount);
    }

    private static IEnumerable<int> GetAllPages(int totalPageCount)
    {
        for (var i = 0; i < totalPageCount; i++)
        {
            yield return i;
        }
    }

    private static void AddPageIndex(ICollection<int> pageIndexes, int pageNumber, int totalPageCount)
    {
        var zeroBasedIndex = pageNumber - 1;
        if (zeroBasedIndex >= 0 && zeroBasedIndex < totalPageCount)
        {
            pageIndexes.Add(zeroBasedIndex);
        }
    }

    private static int NormalizePagesPerSheet(int pagesPerSheet)
        => pagesPerSheet switch
        {
            2 or 4 or 6 or 9 or 16 => pagesPerSheet,
            _ => 1
        };

    private static (int Columns, int Rows) GetGrid(int pagesPerSheet)
        => pagesPerSheet switch
        {
            2 => (1, 2),
            4 => (2, 2),
            6 => (2, 3),
            9 => (3, 3),
            16 => (4, 4),
            _ => (1, 1)
        };
}
