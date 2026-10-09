using AppKit;
using CoreGraphics;
using Foundation;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.MacOS.Infrastructure;

namespace Wino.Mail.MacOS.Views.Mail;

/// <summary>
/// A saved message (.eml) opened from Finder, in its own read-only reader window. Windows renders an
/// opened EML file through MailRenderingPage with a <see cref="MimeMessageInformation"/> (the VM limits
/// the commands to Save As and Print); this window hosts a fresh <see cref="MailRenderingPageViewController"/>
/// the same way <see cref="Calendar.EventDetailsWindow"/> hosts its page. One window per file: opening the
/// same path again brings it forward. Attachments open from a private temporary folder, because the
/// sandbox cannot write next to the .eml; the folder is deleted when the window closes.
/// </summary>
internal sealed class EmlReaderWindow
{
    private static readonly Dictionary<string, EmlReaderWindow> Open = new(StringComparer.OrdinalIgnoreCase);
    private static CGPoint _cascadePoint = CGPoint.Empty;

    private readonly string _path;
    private readonly string _workingFolder;
    private readonly MailRenderingPageViewController _page;
    private readonly NSWindow _window;
    private bool _closed;

    private EmlReaderWindow(string path, string workingFolder, MailRenderingPageViewController page, string title)
    {
        _path = path;
        _workingFolder = workingFolder;
        _page = page;
        _window = new NSWindow(new CGRect(0, 0, 820, 640),
            NSWindowStyle.Titled | NSWindowStyle.Closable | NSWindowStyle.Resizable | NSWindowStyle.Miniaturizable,
            NSBackingStore.Buffered, false)
        {
            Title = title,
            ContentMinSize = new CGSize(640, 480),
            Identifier = "eml-reader",
            RepresentedUrl = NSUrl.FromFilename(path)
        };
        _window.ReleaseWhenClosed(false);
        _window.ContentViewController = page;
        _window.SetContentSize(new CGSize(820, 640));
        // The first window is centred; each further one cascades from the previous.
        if (Open.Count == 0)
        {
            _window.Center();
            _cascadePoint = CGPoint.Empty;
        }
        _cascadePoint = _window.CascadeTopLeftFromPoint(_cascadePoint);
        _window.WillClose += WindowWillClose;
    }

    /// <summary>
    /// Shows the message read from <paramref name="path"/>. Returns false when the bytes are not a
    /// readable message, so the caller can explain why nothing opened.
    /// </summary>
    public static async Task<bool> OpenAsync(IServiceProvider services, string path, byte[] bytes)
    {
        var dispatcher = services.GetRequiredService<IDispatcher>();

        EmlReaderWindow? existing = null;
        await dispatcher.ExecuteOnUIThread(() => Open.TryGetValue(path, out existing));
        if (existing is not null)
        {
            await dispatcher.ExecuteOnUIThread(() => existing._window.MakeKeyAndOrderFront(null));
            return true;
        }

        var workingFolder = Path.Combine(Path.GetTempPath(), "EmlPreview", Guid.NewGuid().ToString("N"));
        MimeMessageInformation information;
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

        // MimeKit is lenient: a file without a single header is not a message, and an empty window helps nobody.
        if (information?.MimeMessage is not { } message || message.Headers.Count == 0)
        {
            DeleteFolder(workingFolder);
            return false;
        }

        var title = string.IsNullOrWhiteSpace(message.Subject) ? Translator.MailItemNoSubject : message.Subject;
        MailRenderingPageViewController page = null!;
        await dispatcher.ExecuteOnUIThread(() => page = services.GetRequiredService<MailRenderingPageViewController>());
        try
        {
            await page.ActivateAsync(NavigationMode.New, information);
        }
        catch
        {
            await dispatcher.ExecuteOnUIThread(page.Dispose);
            DeleteFolder(workingFolder);
            throw;
        }

        await dispatcher.ExecuteOnUIThread(() =>
        {
            var window = new EmlReaderWindow(path, workingFolder, page, title);
            Open[path] = window;
            window._window.MakeKeyAndOrderFront(null);
        });
        return true;
    }

    private async void WindowWillClose(object? sender, EventArgs args)
    {
        if (_closed) return;
        _closed = true;
        _window.WillClose -= WindowWillClose;
        if (Open.TryGetValue(_path, out var owner) && ReferenceEquals(owner, this)) Open.Remove(_path);
        try { await _page.ReleaseAsync(); }
        catch (Exception exception) { Serilog.Log.Warning(exception, "Releasing the message window failed."); }
        finally
        {
            _window.ContentViewController = null!;
            _page.Dispose();
            _window.Dispose();
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
            Serilog.Log.Warning(exception, "Could not delete the message window's temporary folder.");
        }
    }

#if DEBUG
    /// <summary>
    /// "eml-open PATH" opens a message file as Finder would; "eml-windows" lists the open message windows.
    /// The reader's own debug commands (readerbar, reader-*) follow the most recently loaded reader.
    /// </summary>
    public static void RegisterDebugCommands(MacActivationCoordinator coordinator, IDispatcher dispatcher)
    {
        MacDebugBridge.Register("eml-open", async args =>
        {
            var path = string.Join(' ', args);
            if (!File.Exists(path)) return "no file " + path;
            await coordinator.HandleAsync([NSUrl.FromFilename(path)]);
            string result = string.Empty;
            await dispatcher.ExecuteOnUIThread(() => result = $"open={Open.Count}");
            return "ok " + result;
        });
        MacDebugBridge.Register("eml-windows", async _ =>
        {
            string result = string.Empty;
            await dispatcher.ExecuteOnUIThread(() =>
                result = Open.Count == 0 ? "none" : string.Join(" | ", Open.Values.Select(window => $"'{window._window.Title}' {Path.GetFileName(window._path)}")));
            return result;
        });
    }
#endif
}
