using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Printing;
using Windows.Data.Pdf;
using Windows.Graphics.Display;
using Windows.Graphics.Printing;
using Windows.Graphics.Printing.OptionDetails;
using Windows.Storage.Streams;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Printing;

namespace Wino.Core.UWP.Services;

/// <summary>
/// Printer service that uses WinRT APIs to print PDF files.
/// Used modified version of the code here:
/// https://github.com/microsoft/Win2D-Samples/blob/reunion_master/ExampleGallery/PrintingExample.xaml.cs
/// HTML file is saved as PDF to temporary location.
/// Then PDF is loaded as PdfDocument and printed using CanvasBitmap for each page.
/// </summary>
public class PrintService : IPrintService
{
    private TaskCompletionSource<PrintingResult> _taskCompletionSource;
    private CanvasPrintDocument printDocument;
    private PrintTask printTask;
    private PdfDocument pdfDocument;

    private List<CanvasBitmap> bitmaps = new();
    private Vector2 largestBitmap;
    private Vector2 pageSize;
    private Vector2 imagePadding = new Vector2(64, 64);
    private Vector2 cellSize;

    private int bitmapCount;
    private int columns;
    private int rows;
    private int bitmapsPerPage;
    private int pageCount = -1;

    private PrintInformation _currentPrintInformation;

    public async Task<PrintingResult> PrintPdfFileAsync(string pdfFilePath, string printTitle)
    {
        if (_taskCompletionSource != null)
        {
            _taskCompletionSource.TrySetResult(PrintingResult.Abandoned);
            _taskCompletionSource = new TaskCompletionSource<PrintingResult>();
        }

        // Load the PDF file
        var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(pdfFilePath);
        pdfDocument = await PdfDocument.LoadFromFileAsync(file);

        _taskCompletionSource ??= new TaskCompletionSource<PrintingResult>();

        _currentPrintInformation = new PrintInformation(pdfFilePath, printTitle);

        printDocument = new CanvasPrintDocument();
        printDocument.PrintTaskOptionsChanged += OnDocumentTaskOptionsChanged;
        printDocument.Preview += OnDocumentPreview;
        printDocument.Print += OnDocumentPrint;

        var printManager = PrintManager.GetForCurrentView();
        printManager.PrintTaskRequested += PrintingExample_PrintTaskRequested;

        try
        {
            await PrintManager.ShowPrintUIAsync();

            var result = await _taskCompletionSource.Task;

            return result;
        }
        finally
        {
            // Dispose everything.
            UnregisterPrintManager(printManager);
            ClearBitmaps();
            UnregisterTask();
            DisposePDFDocument();

            _taskCompletionSource = null;
        }
    }

    private void DisposePDFDocument()
    {
        if (pdfDocument != null)
        {
            pdfDocument = null;
        }
    }

    private void UnregisterTask()
    {
        if (printTask != null)
        {
            printTask.Completed -= TaskCompleted;
            printTask = null;
        }
    }

    private void UnregisterPrintManager(PrintManager manager)
    {
        manager.PrintTaskRequested -= PrintingExample_PrintTaskRequested;
    }

    private void ClearBitmaps()
    {
        foreach (var bitmap in bitmaps)
        {
            bitmap.Dispose();
        }

        bitmaps.Clear();
    }

    private void PrintingExample_PrintTaskRequested(PrintManager sender, PrintTaskRequestedEventArgs args)
    {
        if (_currentPrintInformation == null) return;

        printTask = args.Request.CreatePrintTask(_currentPrintInformation.PDFTitle, (createPrintTaskArgs) =>
        {
            createPrintTaskArgs.SetSource(printDocument);
        });

        printTask.Completed += TaskCompleted;
    }

    private void TaskCompleted(PrintTask sender, PrintTaskCompletedEventArgs args)
        => _taskCompletionSource?.TrySetResult((PrintingResult)args.Completion);

    private async void OnDocumentTaskOptionsChanged(CanvasPrintDocument sender, CanvasPrintTaskOptionsChangedEventArgs args)
    {
        System.Diagnostics.Debug.WriteLine("[PrintService] OnDocumentTaskOptionsChanged starting...");
        DisplayInformation di = DisplayInformation.GetForCurrentView();
        System.Diagnostics.Debug.WriteLine($"[PrintService] DisplayInformation.RawPixelsPerViewPixel: {di.RawPixelsPerViewPixel}");
        System.Diagnostics.Debug.WriteLine($"[PrintService] DisplayInformation.LogicalDpi: {di.LogicalDpi}");

        var deferral = args.GetDeferral();

        try
        {
            await LoadPDFPageBitmapsAsync(sender);

            var pageDesc = args.PrintTaskOptions.GetPageDescription(1);
            System.Diagnostics.Debug.WriteLine($"[PrintService] pageDesc.PageSize.Width (DIPs): {pageDesc.PageSize.Width}");
            System.Diagnostics.Debug.WriteLine($"[PrintService] pageDesc.PageSize.Height (DIPs): {pageDesc.PageSize.Height}");
            System.Diagnostics.Debug.WriteLine($"[PrintService] pageDesc.DpiX: {pageDesc.DpiX}");
            System.Diagnostics.Debug.WriteLine($"[PrintService] pageDesc.DpiY: {pageDesc.DpiY}");
            var newPageSize = pageDesc.PageSize.ToVector2();

            if (pageSize == newPageSize && pageCount != -1)
            {
                // We've already figured out the pages and the page size hasn't changed, so there's nothing left for us to do here.
                return;
            }

            pageSize = newPageSize;
            sender.InvalidatePreview();

            // Figure out the bitmap index at the top of the current preview page.  We'll request that the preview defaults to showing
            // the page that still has this bitmap on it in the new layout.
            int indexOnCurrentPage = 0;
            if (pageCount != -1)
            {
                indexOnCurrentPage = (int)(args.CurrentPreviewPageNumber - 1) * bitmapsPerPage;
            }

            // Calculate the new layout
            var printablePageSize = pageSize * 0.9f;

            cellSize = largestBitmap + imagePadding;

            var cellsPerPage = printablePageSize / cellSize;

            columns = Math.Max(1, (int)Math.Floor(cellsPerPage.X));
            rows = Math.Max(1, (int)Math.Floor(cellsPerPage.Y));

            bitmapsPerPage = columns * rows;

            // Calculate the page count
            bitmapCount = bitmaps.Count;
            pageCount = (int)Math.Ceiling(bitmapCount / (double)bitmapsPerPage);
            System.Diagnostics.Debug.WriteLine($"[PrintService] Calculated columns: {columns}");
            System.Diagnostics.Debug.WriteLine($"[PrintService] Calculated rows: {rows}");
            System.Diagnostics.Debug.WriteLine($"[PrintService] Calculated bitmapsPerPage: {bitmapsPerPage}");
            System.Diagnostics.Debug.WriteLine($"[PrintService] Calculated pageCount: {pageCount}");
            sender.SetPageCount((uint)pageCount);

            // Set the preview page to the one that has the item that was currently displayed in the last preview
            args.NewPreviewPageNumber = (uint)(indexOnCurrentPage / bitmapsPerPage) + 1;
        }
        finally
        {
            deferral.Complete();
        }
        System.Diagnostics.Debug.WriteLine("[PrintService] OnDocumentTaskOptionsChanged finished.");
    }


    private async Task LoadPDFPageBitmapsAsync(CanvasPrintDocument sender)
    {
        System.Diagnostics.Debug.WriteLine("[PrintService] LoadPDFPageBitmapsAsync starting...");
        DisplayInformation displayInfo = DisplayInformation.GetForCurrentView();
        float rawPixelsPerViewPixel = (float)displayInfo.RawPixelsPerViewPixel;
        if (rawPixelsPerViewPixel == 0)
        {
            System.Diagnostics.Debug.WriteLine($"[PrintService] Warning: RawPixelsPerViewPixel was 0, defaulting to 1.0f");
            rawPixelsPerViewPixel = 1.0f; // Sanity check
        }
        System.Diagnostics.Debug.WriteLine($"[PrintService] DisplayInformation.RawPixelsPerViewPixel: {rawPixelsPerViewPixel}");
        System.Diagnostics.Debug.WriteLine($"[PrintService] DisplayInformation.LogicalDpi: {displayInfo.LogicalDpi}");

        float printerDpi = sender.Dpi;
        if (printerDpi == 0)
        {
            System.Diagnostics.Debug.WriteLine($"[PrintService] Warning: sender.Dpi (printerDpi) was 0, defaulting to 96.0f");
            printerDpi = 96.0f; // Sanity check
        }
        System.Diagnostics.Debug.WriteLine($"[PrintService] sender.Dpi (CanvasPrintDocument.Dpi, used as PrinterDPI): {printerDpi}");

        ClearBitmaps();

        bitmaps ??= new List<CanvasBitmap>();

        for (int i = 0; i < pdfDocument.PageCount; i++)
        {
            var page = pdfDocument.GetPage((uint)i);
            System.Diagnostics.Debug.WriteLine($"[PrintService] Processing page.Index: {page.Index}");
            System.Diagnostics.Debug.WriteLine($"[PrintService] page.Dimensions.MediaBox.Width (PDF points): {page.Dimensions.MediaBox.Width}");
            System.Diagnostics.Debug.WriteLine($"[PrintService] page.Dimensions.MediaBox.Height (PDF points): {page.Dimensions.MediaBox.Height}");

            double pageWidthInPoints = page.Dimensions.MediaBox.Width;
            double pageHeightInPoints = page.Dimensions.MediaBox.Height;

            double pageWidthInInches = pageWidthInPoints / 72.0;
            double pageHeightInInches = pageHeightInPoints / 72.0;

            // Calculate the desired pixel dimensions of the bitmap based on printer DPI
            uint targetPixelWidth = (uint)(pageWidthInInches * printerDpi);
            uint targetPixelHeight = (uint)(pageHeightInInches * printerDpi);

            // Calculate DestinationWidth/Height for PdfPageRenderOptions in DIPs
            PdfPageRenderOptions options = new PdfPageRenderOptions();
            options.DestinationWidth = (uint)(targetPixelWidth / rawPixelsPerViewPixel);
            options.DestinationHeight = (uint)(targetPixelHeight / rawPixelsPerViewPixel);

            System.Diagnostics.Debug.WriteLine($"[PrintService] Page {i}, Calculated PdfPageRenderOptions.DestinationWidth (DIPs): {options.DestinationWidth}, DestinationHeight (DIPs): {options.DestinationHeight}");
            System.Diagnostics.Debug.WriteLine($"[PrintService] Page {i}, TargetPixelWidth: {targetPixelWidth}, TargetPixelHeight: {targetPixelHeight}");

            using (var stream = new InMemoryRandomAccessStream())
            {
                await page.RenderToStreamAsync(stream, options); // Use the options
                var bitmap = await CanvasBitmap.LoadAsync(sender, stream);

                System.Diagnostics.Debug.WriteLine($"[PrintService] bitmap.SizeInPixels.Width: {bitmap.SizeInPixels.Width}");
                System.Diagnostics.Debug.WriteLine($"[PrintService] bitmap.SizeInPixels.Height: {bitmap.SizeInPixels.Height}");
                System.Diagnostics.Debug.WriteLine($"[PrintService] bitmap.Dpi: {bitmap.Dpi}");

                bitmaps.Add(bitmap);
            }
        }

        largestBitmap = Vector2.Zero;

        foreach (var bitmap in bitmaps)
        {
            largestBitmap.X = Math.Max(largestBitmap.X, (float)bitmap.Size.Width);
            largestBitmap.Y = Math.Max(largestBitmap.Y, (float)bitmap.Size.Height);
        }
        System.Diagnostics.Debug.WriteLine("[PrintService] LoadPDFPageBitmapsAsync finished.");
    }


    private void OnDocumentPreview(CanvasPrintDocument sender, CanvasPreviewEventArgs args)
    {
        var ds = args.DrawingSession;
        var pageNumber = args.PageNumber;

        DrawPdfPage(sender, ds, pageNumber);
    }

    private void OnDocumentPrint(CanvasPrintDocument sender, CanvasPrintEventArgs args)
    {
        var detailedOptions = PrintTaskOptionDetails.GetFromPrintTaskOptions(args.PrintTaskOptions);

        int pageCountToPrint = (int)pdfDocument.PageCount;

        for (uint i = 1; i <= pageCountToPrint; ++i)
        {
            using var ds = args.CreateDrawingSession();
            var imageableRect = args.PrintTaskOptions.GetPageDescription(i).ImageableRect;

            DrawPdfPage(sender, ds, i);
        }
    }

    private void DrawPdfPage(CanvasPrintDocument sender, CanvasDrawingSession ds, uint pageNumber)
    {
        if (bitmaps?.Count == 0) return;

        var cellAcross = new Vector2(cellSize.X, 0);
        var cellDown = new Vector2(0, cellSize.Y);

        var totalSize = cellAcross * columns + cellDown * rows;
        Vector2 topLeft = (pageSize - totalSize) / 2;

        int bitmapIndex = ((int)pageNumber - 1) * bitmapsPerPage;

        for (int row = 0; row < rows; ++row)
        {
            for (int column = 0; column < columns; ++column)
            {
                var cellTopLeft = topLeft + cellAcross * column + cellDown * row;
                var bitmapInfo = bitmaps[bitmapIndex % bitmaps.Count];
                var bitmapPos = cellTopLeft + (cellSize - bitmapInfo.Size.ToVector2()) / 2;

                ds.DrawImage(bitmapInfo, bitmapPos);

                bitmapIndex++;
            }
        }
    }

    // --- START SIMULATION CODE ---
    private async Task RunSimulationAsync(float simulatedRawPixelsPerViewPixel, string callingMethodName)
    {
        // --- Simulate LoadPDFPageBitmapsAsync ---
        System.Diagnostics.Debug.WriteLine($"[PrintService] SIMULATING from {callingMethodName} for {simulatedRawPixelsPerViewPixel}");
        System.Diagnostics.Debug.WriteLine("[PrintService] LoadPDFPageBitmapsAsync starting...");

        // Mock DisplayInformation
        float actualRawPixelsPerViewPixel = simulatedRawPixelsPerViewPixel; // Override
        if (actualRawPixelsPerViewPixel == 0)
        {
            System.Diagnostics.Debug.WriteLine($"[PrintService] Warning: actualRawPixelsPerViewPixel was 0, defaulting to 1.0f");
            actualRawPixelsPerViewPixel = 1.0f; // Sanity check
        }
        System.Diagnostics.Debug.WriteLine($"[PrintService] DisplayInformation.RawPixelsPerViewPixel: {actualRawPixelsPerViewPixel}");
        // LogicalDpi is not directly used in calculations we are testing, so we can use a placeholder.
        System.Diagnostics.Debug.WriteLine($"[PrintService] DisplayInformation.LogicalDpi: 96.0f (Simulated)");


        // Mock CanvasPrintDocument sender for DPI
        float printerDpi = 300.0f; // As per subtask
        System.Diagnostics.Debug.WriteLine($"[PrintService] sender.Dpi (CanvasPrintDocument.Dpi, used as PrinterDPI): {printerDpi}");

        // Clear any previous simulation state for bitmaps
        // In a real scenario, ClearBitmaps() would be called. Here we just reset relevant fields.
        this.bitmaps.Clear(); // Assuming 'bitmaps' is the List<CanvasBitmap>
        this.largestBitmap = Vector2.Zero;


        // Simulate pdfDocument.PageCount = 1 and loop once
        int simulatedPageCount = 1;
        for (int i = 0; i < simulatedPageCount; i++)
        {
            // Mock PdfPage
            uint pageIndex = (uint)i;
            double pageWidthInPoints = 612.0;  // Letter size
            double pageHeightInPoints = 792.0; // Letter size

            System.Diagnostics.Debug.WriteLine($"[PrintService] Processing page.Index: {pageIndex}");
            System.Diagnostics.Debug.WriteLine($"[PrintService] page.Dimensions.MediaBox.Width (PDF points): {pageWidthInPoints}");
            System.Diagnostics.Debug.WriteLine($"[PrintService] page.Dimensions.MediaBox.Height (PDF points): {pageHeightInPoints}");

            double pageWidthInInches = pageWidthInPoints / 72.0;
            double pageHeightInInches = pageHeightInPoints / 72.0;

            uint targetPixelWidth = (uint)(pageWidthInInches * printerDpi);
            uint targetPixelHeight = (uint)(pageHeightInInches * printerDpi);

            PdfPageRenderOptions options = new PdfPageRenderOptions();
            options.DestinationWidth = (uint)(targetPixelWidth / actualRawPixelsPerViewPixel);
            options.DestinationHeight = (uint)(targetPixelHeight / actualRawPixelsPerViewPixel);

            System.Diagnostics.Debug.WriteLine($"[PrintService] Page {i}, Calculated PdfPageRenderOptions.DestinationWidth (DIPs): {options.DestinationWidth}, DestinationHeight (DIPs): {options.DestinationHeight}");
            System.Diagnostics.Debug.WriteLine($"[PrintService] Page {i}, TargetPixelWidth: {targetPixelWidth}, TargetPixelHeight: {targetPixelHeight}");

            // Mock CanvasBitmap properties (as we can't actually render)
            uint mockBitmapSizeInPixelsWidth = targetPixelWidth;
            uint mockBitmapSizeInPixelsHeight = targetPixelHeight;
            float mockBitmapDpi = printerDpi;

            System.Diagnostics.Debug.WriteLine($"[PrintService] bitmap.SizeInPixels.Width: {mockBitmapSizeInPixelsWidth} (Simulated)");
            System.Diagnostics.Debug.WriteLine($"[PrintService] bitmap.SizeInPixels.Height: {mockBitmapSizeInPixelsHeight} (Simulated)");
            System.Diagnostics.Debug.WriteLine($"[PrintService] bitmap.Dpi: {mockBitmapDpi} (Simulated)");

            // Simulate adding to bitmaps list and tracking largest
            // We don't add a real CanvasBitmap, just update what's needed for OnDocumentTaskOptionsChanged
            this.largestBitmap.X = Math.Max(this.largestBitmap.X, mockBitmapSizeInPixelsWidth);
            this.largestBitmap.Y = Math.Max(this.largestBitmap.Y, mockBitmapSizeInPixelsHeight);
            // bitmaps.Add(null); // Not adding real bitmaps
        }
        this.bitmapCount = simulatedPageCount; // Set based on simulation

        System.Diagnostics.Debug.WriteLine("[PrintService] LoadPDFPageBitmapsAsync finished.");

        // --- Simulate OnDocumentTaskOptionsChanged ---
        System.Diagnostics.Debug.WriteLine("[PrintService] OnDocumentTaskOptionsChanged starting...");
        // DisplayInformation logs already done in LoadPDFPageBitmapsAsync simulation part

        // Mock PageDescription (from PrintTaskOptions)
        // For Letter paper (8.5x11 inches) at 300 DPI:
        // Pixel size = (8.5*300) x (11*300) = 2550 x 3300 pixels
        // DIP size = (PixelSize / rawPixelsPerViewPixel)
        float pageDescPageSizeWidth = (2550.0f / actualRawPixelsPerViewPixel);
        float pageDescPageSizeHeight = (3300.0f / actualRawPixelsPerViewPixel);
        float pageDescDpiX = 300.0f;
        float pageDescDpiY = 300.0f;

        System.Diagnostics.Debug.WriteLine($"[PrintService] pageDesc.PageSize.Width (DIPs): {pageDescPageSizeWidth} (Simulated)");
        System.Diagnostics.Debug.WriteLine($"[PrintService] pageDesc.PageSize.Height (DIPs): {pageDescPageSizeHeight} (Simulated)");
        System.Diagnostics.Debug.WriteLine($"[PrintService] pageDesc.DpiX: {pageDescDpiX} (Simulated)");
        System.Diagnostics.Debug.WriteLine($"[PrintService] pageDesc.DpiY: {pageDescDpiY} (Simulated)");

        this.pageSize = new Vector2(pageDescPageSizeWidth, pageDescPageSizeHeight);
        // sender.InvalidatePreview(); // Cannot call

        // Calculate the new layout
        var printablePageSize = this.pageSize * 0.9f; // Assuming default behavior
        this.imagePadding = new Vector2(64, 64); // Default from class
        this.cellSize = this.largestBitmap + this.imagePadding;

        var cellsPerPage = printablePageSize / this.cellSize;

        this.columns = Math.Max(1, (int)Math.Floor(cellsPerPage.X));
        this.rows = Math.Max(1, (int)Math.Floor(cellsPerPage.Y));
        this.bitmapsPerPage = this.columns * this.rows;
        this.pageCount = (int)Math.Ceiling(this.bitmapCount / (double)this.bitmapsPerPage);

        System.Diagnostics.Debug.WriteLine($"[PrintService] Calculated columns: {this.columns}");
        System.Diagnostics.Debug.WriteLine($"[PrintService] Calculated rows: {this.rows}");
        System.Diagnostics.Debug.WriteLine($"[PrintService] Calculated bitmapsPerPage: {this.bitmapsPerPage}");
        System.Diagnostics.Debug.WriteLine($"[PrintService] Calculated pageCount: {this.pageCount}");
        // sender.SetPageCount((uint)this.pageCount); // Cannot call

        System.Diagnostics.Debug.WriteLine("[PrintService] OnDocumentTaskOptionsChanged finished.");
        System.Diagnostics.Debug.WriteLine($"[PrintService] SIMULATION END for {simulatedRawPixelsPerViewPixel}");
        System.Diagnostics.Debug.WriteLine("---"); // Separator
    }

    public async Task SimulatePrintScalingAsync()
    {
        // Temporarily store and clear global state that might interfere
        var originalBitmaps = new List<CanvasBitmap>(this.bitmaps);
        var originalLargestBitmap = this.largestBitmap;
        var originalPageSize = this.pageSize;
        var originalCellSize = this.cellSize;
        int originalBitmapCount = this.bitmapCount;
        int originalColumns = this.columns;
        int originalRows = this.rows;
        int originalBitmapsPerPage = this.bitmapsPerPage;
        int originalPageCount = this.pageCount;


        await RunSimulationAsync(1.0f, nameof(SimulatePrintScalingAsync));
        await RunSimulationAsync(1.25f, nameof(SimulatePrintScalingAsync));
        await RunSimulationAsync(1.5f, nameof(SimulatePrintScalingAsync));

        // Restore original state if necessary, though for this task it's just about logs
        this.bitmaps = originalBitmaps;
        this.largestBitmap = originalLargestBitmap;
        this.pageSize = originalPageSize;
        this.cellSize = originalCellSize;
        this.bitmapCount = originalBitmapCount;
        this.columns = originalColumns;
        this.rows = originalRows;
        this.bitmapsPerPage = originalBitmapsPerPage;
        this.pageCount = originalPageCount;
    }
    // --- END SIMULATION CODE ---
}
