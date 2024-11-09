using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Printing;
using Windows.Data.Pdf;
using Windows.Graphics.Printing;
using Windows.Graphics.Printing.OptionDetails;
using Windows.Storage.Streams;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Printing;

namespace Wino.Core.UWP.Services
{
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
            var deferral = args.GetDeferral();

            try
            {
                await LoadPDFPageBitmapsAsync(sender);

                var pageDesc = args.PrintTaskOptions.GetPageDescription(1);
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
                sender.SetPageCount((uint)pageCount);

                // Set the preview page to the one that has the item that was currently displayed in the last preview
                args.NewPreviewPageNumber = (uint)(indexOnCurrentPage / bitmapsPerPage) + 1;
            }
            finally
            {
                deferral.Complete();
            }
        }


        private async Task LoadPDFPageBitmapsAsync(CanvasPrintDocument sender)
        {
            ClearBitmaps();

            bitmaps ??= new List<CanvasBitmap>();

            for (int i = 0; i < pdfDocument.PageCount; i++)
            {
                var page = pdfDocument.GetPage((uint)i);
                var stream = new InMemoryRandomAccessStream();
                await page.RenderToStreamAsync(stream);
                var bitmap = await CanvasBitmap.LoadAsync(sender, stream);
                bitmaps.Add(bitmap);
            }

            largestBitmap = Vector2.Zero;

            foreach (var bitmap in bitmaps)
            {
                largestBitmap.X = Math.Max(largestBitmap.X, (float)bitmap.Size.Width);
                largestBitmap.Y = Math.Max(largestBitmap.Y, (float)bitmap.Size.Height);
            }
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
    }
}
