using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Printing;
using Windows.Data.Pdf;
using Windows.Graphics.Printing;
using Windows.Graphics.Printing.OptionDetails;
using WinRT.Interop;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Printing;
using Wino.Mail.WinUI.Interfaces;
using Wino.Mail.ViewModels.Models.Printing;
using DomainPrintColorMode = Wino.Core.Domain.Enums.PrintColorMode;
using DomainPrintMediaSize = Wino.Core.Domain.Enums.PrintMediaSize;
using DomainPrintOrientation = Wino.Core.Domain.Enums.PrintOrientation;
using WinRT;

namespace Wino.Mail.WinUI.Services;

/// <summary>
/// Printer service that uses the WinRT print preview UI with a WebView2-backed PDF render callback.
/// </summary>
public class PrintService : IWindowsPrintService
{
    private const float PdfRenderDpi = 300f;
    private const float DefaultDpi = 96f;

    private const string MarginsOptionId = "WinoMargins";
    private const string ScaleOptionId = "WinoScale";
    private const string BackgroundsOptionId = "WinoBackgrounds";
    private const string HeadersAndFootersOptionId = "WinoHeadersAndFooters";

    private const string MarginsNormal = "Normal";
    private const string MarginsModerate = "Moderate";
    private const string MarginsNarrow = "Narrow";
    private const string MarginsNone = "None";
    private const int DefaultScalePercentage = 100;
    private static readonly int[] ScalePercentages = [50, 75, 90, 100, 110, 125, 150, 200];

    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private readonly SemaphoreSlim _renderGate = new(1, 1);
    private TaskCompletionSource<PrintingResult>? _taskCompletionSource;
    private CanvasPrintDocument? _printDocument;
    private PrintTask? _printTask;
    private PrintTaskOptionDetails? _printTaskOptionDetails;
    private PrintManager? _printManager;
    private PdfDocument? _pdfDocument;
    private Func<MailPrintOptions, Task<Stream>>? _renderPdfStreamAsync;
    private MailPrintOptions _currentRenderSettings = new();
    private string _printTitle = string.Empty;

    private readonly List<CanvasBitmap> _bitmaps = new();
    private Windows.Foundation.Rect _imageableRect;
    private int _pagesPerSheet = 1;
    private int _columns = 1;
    private int _rows = 1;
    private int _sheetCount;
    private bool _isGrayscale;
    private bool _previewFailed;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint windowHandle);

    public async Task<PrintingResult> PrintAsync(nint windowHandle, string printTitle, Func<MailPrintOptions, Task<Stream>> renderPdfStreamAsync, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (windowHandle == IntPtr.Zero || !IsWindow(windowHandle))
            return PrintingResult.Unavailable;

        ArgumentNullException.ThrowIfNull(renderPdfStreamAsync);
        ArgumentNullException.ThrowIfNull(printTitle);

        // A singleton cannot replace an active native print session safely.
        if (!await _sessionGate.WaitAsync(0, cancellationToken))
            return PrintingResult.Unavailable;

        try
        {
            var completion = new TaskCompletionSource<PrintingResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _taskCompletionSource = completion;
            _renderPdfStreamAsync = renderPdfStreamAsync ?? throw new ArgumentNullException(nameof(renderPdfStreamAsync));
            _printTitle = printTitle ?? throw new ArgumentNullException(nameof(printTitle));
            _currentRenderSettings = new MailPrintOptions();
            _previewFailed = false;

            _printDocument = new CanvasPrintDocument();
            _printDocument.PrintTaskOptionsChanged += OnDocumentTaskOptionsChanged;
            _printDocument.Preview += OnDocumentPreview;
            _printDocument.Print += OnDocumentPrint;

            _printManager = PrintManagerInterop.GetForWindow(windowHandle);
            _printManager.PrintTaskRequested += OnPrintTaskRequested;

            await ReloadPdfDocumentAsync(_currentRenderSettings);

            if (cancellationToken.IsCancellationRequested)
                return PrintingResult.Canceled;

            if (!await PrintManagerInterop.ShowPrintUIForWindowAsync(windowHandle))
                return PrintingResult.Abandoned;

            // WinRT exposes no cancellation for an opened print UI. Retain the
            // document and singleton gate until native completion, then report cancellation.
            var result = await completion.Task;
            return cancellationToken.IsCancellationRequested ? PrintingResult.Canceled
                : _previewFailed ? PrintingResult.Failed : result;
        }
        finally
        {
            try
            {
                await _renderGate.WaitAsync();
                try { CleanupPrintSession(); }
                finally { _renderGate.Release(); }
            }
            finally { _sessionGate.Release(); }
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
        _sheetCount = 0;
        _isGrayscale = false;
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
        if (!ReferenceEquals(sender, _printManager))
            return;

        var document = _printDocument;
        _printTask = args.Request.CreatePrintTask(_printTitle, createPrintTaskArgs =>
        {
            if (document == null)
                return;

            createPrintTaskArgs.SetSource(document);
        });

        _printTask.Completed += OnPrintTaskCompleted;

        // The Pages option offers a custom set of pages only when the task allows it.
        var pageRangeOptions = _printTask.Options.PageRangeOptions;
        pageRangeOptions.AllowAllPages = true;
        pageRangeOptions.AllowCustomSetOfPages = true;

        _printTaskOptionDetails = PrintTaskOptionDetails.GetFromPrintTaskOptions(_printTask.Options);
        _printTaskOptionDetails.DisplayedOptions.Clear();

        // Windows leaves out the options that the selected printer does not support.
        TryAddDisplayedOption(StandardPrintTaskOptions.Copies);
        TryAddDisplayedOption(StandardPrintTaskOptions.CustomPageRanges);
        TryAddDisplayedOption(StandardPrintTaskOptions.Orientation);
        TryAddDisplayedOption(StandardPrintTaskOptions.MediaSize);
        TryAddDisplayedOption(StandardPrintTaskOptions.MediaType);
        TryAddDisplayedOption(StandardPrintTaskOptions.InputBin);
        TryAddDisplayedOption(StandardPrintTaskOptions.ColorMode);
        TryAddDisplayedOption(StandardPrintTaskOptions.PrintQuality);
        TryAddDisplayedOption(StandardPrintTaskOptions.Duplex);
        TryAddDisplayedOption(StandardPrintTaskOptions.Collation);
        TryAddDisplayedOption(StandardPrintTaskOptions.NUp);
        TryAddDisplayedOption(StandardPrintTaskOptions.Bordering);
        TryAddDisplayedOption(StandardPrintTaskOptions.Binding);
        TryAddDisplayedOption(StandardPrintTaskOptions.Staple);
        TryAddDisplayedOption(StandardPrintTaskOptions.HolePunch);
        AddCustomOptions(_printTaskOptionDetails);

        _printTaskOptionDetails.OptionChanged += OnPrintTaskOptionChanged;
    }

    private static void AddCustomOptions(PrintTaskOptionDetails optionDetails)
    {
        var defaults = new MailPrintOptions();

        var margins = optionDetails.CreateItemListOption(MarginsOptionId, Translator.PrintOptions_Margins);
        margins.AddItem(MarginsNormal, Translator.PrintOptions_MarginsNormal);
        margins.AddItem(MarginsModerate, Translator.PrintOptions_MarginsModerate);
        margins.AddItem(MarginsNarrow, Translator.PrintOptions_MarginsNarrow);
        margins.AddItem(MarginsNone, Translator.PrintOptions_MarginsNone);
        margins.TrySetValue(MarginsNormal);
        optionDetails.DisplayedOptions.Add(MarginsOptionId);

        var scale = optionDetails.CreateItemListOption(ScaleOptionId, Translator.PrintOptions_Scale);
        foreach (var percentage in ScalePercentages)
        {
            scale.AddItem(percentage.ToString(CultureInfo.InvariantCulture), string.Format(CultureInfo.CurrentCulture, "{0}%", percentage));
        }

        scale.TrySetValue(DefaultScalePercentage.ToString(CultureInfo.InvariantCulture));
        optionDetails.DisplayedOptions.Add(ScaleOptionId);

        var backgrounds = optionDetails.CreateToggleOption(BackgroundsOptionId, Translator.PrintOptions_Backgrounds);
        backgrounds.TrySetValue(defaults.ShouldPrintBackgrounds);
        optionDetails.DisplayedOptions.Add(BackgroundsOptionId);

        var headersAndFooters = optionDetails.CreateToggleOption(HeadersAndFootersOptionId, Translator.PrintOptions_HeadersAndFooters);
        headersAndFooters.TrySetValue(defaults.ShouldPrintHeaderAndFooter);
        optionDetails.DisplayedOptions.Add(HeadersAndFootersOptionId);
    }

    private void OnPrintTaskCompleted(PrintTask sender, PrintTaskCompletedEventArgs args)
    {
        if (!ReferenceEquals(sender, _printTask))
            return;

        _taskCompletionSource?.TrySetResult(args.Completion switch
        {
            PrintTaskCompletion.Submitted => PrintingResult.Submitted,
            PrintTaskCompletion.Canceled => PrintingResult.Canceled,
            PrintTaskCompletion.Failed => PrintingResult.Failed,
            _ => PrintingResult.Abandoned
        });
    }

    private void OnPrintTaskOptionChanged(PrintTaskOptionDetails sender, PrintTaskOptionChangedEventArgs args)
        => _printDocument?.InvalidatePreview();

    private async void OnDocumentTaskOptionsChanged(CanvasPrintDocument sender, CanvasPrintTaskOptionsChangedEventArgs args)
    {
        if (!ReferenceEquals(sender, _printDocument))
            return;

        var deferral = args.GetDeferral();

        try
        {
            var newSettings = CreateRenderSettings(args.PrintTaskOptions);
            _isGrayscale = newSettings.ColorMode == DomainPrintColorMode.Grayscale;

            if (ShouldReloadPdf(newSettings))
            {
                await ReloadPdfDocumentAsync(newSettings);
            }
            else
            {
                _currentRenderSettings = newSettings;
            }

            if (!ReferenceEquals(sender, _printDocument))
                return;

            UpdatePreviewLayout(args.PrintTaskOptions);
            sender.InvalidatePreview();
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Failed to refresh the native print preview.");
            if (ReferenceEquals(sender, _printDocument))
                _previewFailed = true;
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async Task ReloadPdfDocumentAsync(MailPrintOptions settings)
    {
        var session = _taskCompletionSource;
        await _renderGate.WaitAsync();
        try
        {
            if (session == null || !ReferenceEquals(session, _taskCompletionSource))
                return;
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
        finally { _renderGate.Release(); }
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

        _imageableRect = GetImageableRect(printTaskOptions);

        _pagesPerSheet = GetPagesPerSheet();
        (_columns, _rows) = GetGrid(_pagesPerSheet);

        // Page ranges count preview pages, which are sheets, so the numbers typed into Pages match the preview.
        _sheetCount = Math.Max(1, (int)Math.Ceiling(_bitmaps.Count / (double)_pagesPerSheet));

        _printDocument?.SetPageCount((uint)_sheetCount);
    }

    private void OnDocumentPreview(CanvasPrintDocument sender, CanvasPreviewEventArgs args)
        => DrawSheet(args.DrawingSession, args.PageNumber);

    private void OnDocumentPrint(CanvasPrintDocument sender, CanvasPrintEventArgs args)
    {
        if (_pdfDocument == null || _sheetCount == 0)
            return;

        foreach (var sheetNumber in GetSheetsToPrint(args.PrintTaskOptions))
        {
            using var drawingSession = args.CreateDrawingSession();
            DrawSheet(drawingSession, sheetNumber);
        }
    }

    private List<uint> GetSheetsToPrint(PrintTaskOptions printTaskOptions)
    {
        var sheets = new List<uint>();
        var added = new HashSet<uint>();

        IList<PrintPageRange>? ranges = null;
        try
        {
            ranges = printTaskOptions.CustomPageRanges;
        }
        catch (COMException)
        {
        }

        if (ranges != null)
        {
            foreach (var range in ranges)
            {
                var first = Math.Max(1, Math.Min(range.FirstPageNumber, range.LastPageNumber));
                var last = Math.Min(_sheetCount, Math.Max(range.FirstPageNumber, range.LastPageNumber));

                for (var sheet = first; sheet <= last; sheet++)
                {
                    if (added.Add((uint)sheet))
                        sheets.Add((uint)sheet);
                }
            }
        }

        if (sheets.Count == 0)
        {
            for (uint sheet = 1; sheet <= _sheetCount; sheet++)
                sheets.Add(sheet);
        }

        return sheets;
    }

    private void DrawSheet(CanvasDrawingSession drawingSession, uint pageNumber)
    {
        if (_bitmaps.Count == 0)
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
                if (pageIndex >= _bitmaps.Count)
                    return;

                var bitmap = _bitmaps[pageIndex];
                var cellTopLeft = topLeft + new Vector2(cellWidth * column, cellHeight * row);
                DrawBitmapInCell(drawingSession, bitmap, cellTopLeft, new Vector2(cellWidth, cellHeight), _isGrayscale);
                pageIndex++;
            }
        }
    }

    private static void DrawBitmapInCell(CanvasDrawingSession drawingSession, CanvasBitmap bitmap, Vector2 cellTopLeft, Vector2 cellSize, bool grayscale)
    {
        var bitmapSize = bitmap.Size.ToVector2();
        var scale = Math.Min(cellSize.X / bitmapSize.X, cellSize.Y / bitmapSize.Y);
        var targetSize = bitmapSize * scale;
        var targetOffset = cellTopLeft + (cellSize - targetSize) / 2;
        var destination = new Windows.Foundation.Rect(targetOffset.X, targetOffset.Y, targetSize.X, targetSize.Y);

        if (!grayscale)
        {
            drawingSession.DrawImage(bitmap, destination);
            return;
        }

        // Applied here as well, because not every driver honors the color mode in the print ticket.
        using var effect = new GrayscaleEffect { Source = bitmap };
        drawingSession.DrawImage(effect, destination, bitmap.Bounds);
    }

    private static Windows.Foundation.Rect GetImageableRect(PrintTaskOptions printTaskOptions)
    {
        var pageDescription = printTaskOptions.GetPageDescription(1);
        var imageableRect = pageDescription.ImageableRect;

        if (imageableRect.Width > 0 && imageableRect.Height > 0)
            return imageableRect;

        return new Windows.Foundation.Rect(0, 0, pageDescription.PageSize.Width, pageDescription.PageSize.Height);
    }

    private MailPrintOptions CreateRenderSettings(PrintTaskOptions printTaskOptions)
    {
        var imageableRect = GetImageableRect(printTaskOptions);
        var margin = GetMargin();

        // Each PDF page is rendered at the printable area of one sheet, so it fills the paper without being shrunk.
        return new()
        {
            Orientation = GetOrientation(printTaskOptions),
            MediaSize = GetMediaSize(),
            ColorMode = IsGrayscale() ? DomainPrintColorMode.Grayscale : DomainPrintColorMode.Color,
            PageWidth = imageableRect.Width / DefaultDpi,
            PageHeight = imageableRect.Height / DefaultDpi,
            MarginTop = margin,
            MarginBottom = margin,
            MarginLeft = margin,
            MarginRight = margin,
            ShouldPrintBackgrounds = GetToggleValue(BackgroundsOptionId, _currentRenderSettings.ShouldPrintBackgrounds),
            ShouldPrintSelectionOnly = _currentRenderSettings.ShouldPrintSelectionOnly,
            ShouldPrintHeaderAndFooter = GetToggleValue(HeadersAndFootersOptionId, _currentRenderSettings.ShouldPrintHeaderAndFooter),
            HeaderTitle = _currentRenderSettings.HeaderTitle,
            FooterUri = _currentRenderSettings.FooterUri,
            ScaleFactor = GetScaleFactor()
        };
    }

    private static bool AreClose(double left, double right) => Math.Abs(left - right) < 0.01;

    private bool ShouldReloadPdf(MailPrintOptions newSettings)
        => newSettings.Orientation != _currentRenderSettings.Orientation
           || newSettings.MediaSize != _currentRenderSettings.MediaSize
           || !AreClose(newSettings.PageWidth, _currentRenderSettings.PageWidth)
           || !AreClose(newSettings.PageHeight, _currentRenderSettings.PageHeight)
           || !AreClose(newSettings.MarginTop, _currentRenderSettings.MarginTop)
           || !AreClose(newSettings.MarginBottom, _currentRenderSettings.MarginBottom)
           || !AreClose(newSettings.MarginLeft, _currentRenderSettings.MarginLeft)
           || !AreClose(newSettings.MarginRight, _currentRenderSettings.MarginRight)
           || !AreClose(newSettings.ScaleFactor, _currentRenderSettings.ScaleFactor)
           || newSettings.ShouldPrintBackgrounds != _currentRenderSettings.ShouldPrintBackgrounds
           || newSettings.ShouldPrintHeaderAndFooter != _currentRenderSettings.ShouldPrintHeaderAndFooter
           || !string.Equals(newSettings.HeaderTitle, _currentRenderSettings.HeaderTitle, StringComparison.Ordinal)
           || !string.Equals(newSettings.FooterUri, _currentRenderSettings.FooterUri, StringComparison.Ordinal);

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

    private double GetMargin()
        => GetOptionValue(MarginsOptionId)?.ToString() switch
        {
            MarginsModerate => 0.5,
            MarginsNarrow => 0.25,
            MarginsNone => 0,
            _ => new MailPrintOptions().MarginTop
        };

    private double GetScaleFactor()
    {
        var value = GetOptionValue(ScaleOptionId)?.ToString();
        var percentage = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : DefaultScalePercentage;

        return Math.Clamp(percentage / 100d, 0.1, 2.0);
    }

    private bool GetToggleValue(string optionId, bool fallback)
        => GetOptionValue(optionId) is bool value ? value : fallback;

    private bool IsGrayscale()
    {
        var value = GetOptionValue(StandardPrintTaskOptions.ColorMode)?.ToString();
        return string.Equals(value, nameof(Windows.Graphics.Printing.PrintColorMode.Grayscale), StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, nameof(Windows.Graphics.Printing.PrintColorMode.Monochrome), StringComparison.OrdinalIgnoreCase);
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

    [DynamicWindowsRuntimeCast(typeof(PrintCopiesOptionDetails))]
    [DynamicWindowsRuntimeCast(typeof(PrintPageRangeOptionDetails))]
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
                _ => option?.Value
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
