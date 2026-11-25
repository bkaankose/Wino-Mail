#if DEBUG
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Printing;
using Wino.Core.Domain.Models.Menus;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// <c>debug-reader-pdf</c> <c>s</c> path (the presenter's export, no picker), <c>debug-reader-print</c> (opens the print
/// dialog; the result is logged when it closes), <c>debug-reader-source</c> (the View message source operation).
/// </summary>
internal static partial class ReaderDebugActions
{
    static partial void RegisterPrint(Adw.Application application, Func<IServiceProvider?> services)
    {
        Add(application, "debug-reader-pdf", "s", value => Observe(PdfAsync(value)));
        Add(application, "debug-reader-print", null, _ => Observe(PrintAsync()));
        Add(application, "debug-reader-source", null, _ => Observe(Current().ViewModel.OperationClickedCommand.ExecuteAsync(MailOperationMenuItem.Create(MailOperation.ViewMessageSource))));
    }

    private static async Task PdfAsync(string path)
    {
        var presenter = Current().ViewModel.PrintPresenter ?? throw new InvalidOperationException("The reader has no print presenter.");
        var result = await presenter.ExportPdfAsync(path);
        Serilog.Log.Information("Debug action: reader pdf {Status} {Error}", result.Status, result.ErrorMessage);
    }

    private static async Task PrintAsync()
    {
        var view = Current();
        var presenter = view.ViewModel.PrintPresenter ?? throw new InvalidOperationException("The reader has no print presenter.");
        var result = await presenter.PrintAsync(new MailPrintRequest(view.ViewModel.Subject ?? string.Empty));
        Serilog.Log.Information("Debug action: reader print {Result}", result);
    }
}
#endif
