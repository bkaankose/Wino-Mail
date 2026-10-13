using Microsoft.Extensions.DependencyInjection;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Mail;

/// <summary>
/// A saved message (.eml) opened from Files or another app (Mac EmlReaderWindow): a fresh
/// <see cref="MailRenderingPageViewController"/> over a <see cref="MimeMessageInformation"/> (the ViewModel limits the
/// commands to Save As and Print) in a sheet with Done, full screen on iPhone and a page sheet on iPad. One sheet at a
/// time; opening the same path again keeps the open sheet. Attachments open from a private temporary folder that is
/// deleted when the sheet closes. <c>IApplicationShellHost.OpenMailFileAsync</c> calls <see cref="OpenAsync"/>.
/// </summary>
public sealed class EmlReaderSheet
{
    private static EmlReaderSheet? _open;

    private readonly string _path;
    private readonly string _workingFolder;
    private readonly MailRenderingPageViewController _page;
    private readonly IUIKitShellHost _host;
    private UINavigationController? _sheet;
    private bool _closed;

    private EmlReaderSheet(string path, string workingFolder, MailRenderingPageViewController page, IUIKitShellHost host)
    {
        _path = path;
        _workingFolder = workingFolder;
        _page = page;
        _host = host;
    }

    /// <summary>The page of the open sheet, for the debug bridge.</summary>
    internal static MailRenderingPageViewController? OpenPage => _open?._page;

    /// <summary>
    /// Shows the message read from <paramref name="path"/>. Returns false when the bytes are not a readable message, so
    /// the caller can explain why nothing opened.
    /// </summary>
    public static async Task<bool> OpenAsync(IServiceProvider services, string path, byte[] bytes)
    {
        var dispatcher = services.GetRequiredService<IDispatcher>();
        var host = services.GetRequiredService<IUIKitShellHost>();

        bool alreadyOpen = false;
        await dispatcher.ExecuteOnUIThread(() => alreadyOpen = _open is { _closed: false } open && string.Equals(open._path, path, StringComparison.OrdinalIgnoreCase));
        if (alreadyOpen) return true;
        if (_open is { } previous) await previous.CloseAsync();

        var workingFolder = Path.Combine(Path.GetTempPath(), "EmlPreview", Guid.NewGuid().ToString("N"));
        MimeMessageInformation? information;
        try
        {
            Directory.CreateDirectory(workingFolder);
            information = await services.GetRequiredService<IMimeFileService>().GetMimeMessageInformationAsync(bytes, workingFolder).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Serilog.Log.Warning(exception, "Could not parse the opened message file.");
            DeleteFolder(workingFolder);
            return false;
        }

        // MimeKit is lenient: a file without a single header is not a message.
        if (information?.MimeMessage is not { } message || message.Headers.Count == 0)
        {
            DeleteFolder(workingFolder);
            return false;
        }

        MailRenderingPageViewController? created = null;
        await dispatcher.ExecuteOnUIThread(() => created = services.GetRequiredService<MailRenderingPageViewController>());
        if (created is not { } page)
        {
            DeleteFolder(workingFolder);
            return false;
        }
        var sheet = new EmlReaderSheet(path, workingFolder, page, host);
        await dispatcher.ExecuteOnUIThread(() =>
        {
            _open = sheet;
            page.CloseOverride = () => _ = sheet.CloseAsync();
            // The command bar sits on top of the sheet on both idioms: there is no shell toolbar here.
            page.ShowsCommandBar = true;
            page.NavigationItem.Title = string.IsNullOrWhiteSpace(message.Subject) ? Translator.MailItemNoSubject : message.Subject;
            page.NavigationItem.RightBarButtonItem = new UIBarButtonItem(UIBarButtonSystemItem.Done, (_, _) => _ = sheet.CloseAsync());
            var phone = UIDevice.CurrentDevice.UserInterfaceIdiom == UIUserInterfaceIdiom.Phone;
            sheet._sheet = host.PresentSheet(page, phone ? UIKitPageHost.FullScreenSheet : UIKitPageHost.Sheet, () => _ = sheet.ReleaseAsync());
        });
        try
        {
            await page.ActivateAsync(NavigationMode.New, information);
        }
        catch
        {
            await sheet.CloseAsync();
            throw;
        }
        return true;
    }

    /// <summary>Dismisses the sheet (Done, or the reader's own close) and releases the page.</summary>
    private async Task CloseAsync()
    {
        if (_sheet is { } sheet && !_closed)
        {
            try { await _host.DismissSheetAsync(sheet); }
            catch (Exception exception) { Serilog.Log.Warning(exception, "Dismissing the message sheet failed."); }
        }
        await ReleaseAsync();
    }

    private async Task ReleaseAsync()
    {
        if (_closed) return;
        _closed = true;
        if (ReferenceEquals(_open, this)) _open = null;
        try { await _page.ReleaseAsync(); }
        catch (Exception exception) { Serilog.Log.Warning(exception, "Releasing the message sheet failed."); }
        finally
        {
            var folder = _workingFolder;
            _ = Task.Run(() => DeleteFolder(folder));
        }
    }

    private static void DeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch (Exception exception)
        {
            Serilog.Log.Warning(exception, "Could not delete the message sheet's temporary folder.");
        }
    }
}
