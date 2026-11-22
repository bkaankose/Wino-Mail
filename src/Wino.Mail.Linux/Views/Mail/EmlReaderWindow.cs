using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Linux.Infrastructure;
using Wino.Platform.Linux;
using Wino.Services.Interfaces;
using Wino.Services.Models.MailItem;

namespace Wino.Mail.Linux.Views.Mail;

/// <summary>A saved message (<c>.eml</c>) in its own read-only reader window, one per path (macOS <c>EmlReaderWindow</c>).</summary>
internal static class EmlReaderWindow
{
    private const string PreviewFolderName = "EmlPreview";
    private static bool _cleaned;

    /// <summary>False when the bytes are not a readable message, so the caller explains why nothing opened.</summary>
    public static async Task<bool> OpenAsync(IServiceProvider services, string path, byte[] bytes)
    {
        var dispatcher = services.GetRequiredService<IDispatcher>();
        var registry = services.GetRequiredService<ReaderWindowRegistry>();
        var key = ReaderWindowRegistry.FileKey(path);
        var presented = false;
        await dispatcher.ExecuteOnUIThread(() => presented = registry.TryPresent(key));
        if (presented) return true;

        var root = Path.Combine(LinuxPaths.FromEnvironment().TemporaryRoot, PreviewFolderName);
        if (!_cleaned)
        {
            _cleaned = true;
            DeleteFolder(root);
        }
        var workingFolder = Path.Combine(root, Guid.NewGuid().ToString("N"));
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
        // MimeKit is lenient: a file without a header is not a message.
        if (information?.MimeMessage is not { } message || message.Headers.Count == 0)
        {
            DeleteFolder(workingFolder);
            return false;
        }

        var title = string.IsNullOrWhiteSpace(message.Subject) ? Translator.MailItemNoSubject : message.Subject;
        MailReaderView? view = null;
        await dispatcher.ExecuteOnUIThread(() =>
        {
            view = PageScopes.Create<MailReaderView>(services);
            view.HostKind = ReaderHostKind.File;
        });
        if (view is null) return false;
        try { await view.ActivateAsync(NavigationMode.New, information); }
        catch
        {
            await view.ReleaseAsync();
            DeleteFolder(workingFolder);
            throw;
        }

        var opened = false;
        await dispatcher.ExecuteOnUIThread(() =>
        {
            if (Gio.Application.GetDefault() is not Gtk.Application application) return;
            var window = new SecondaryWindow(application, ReaderWindowRegistry.Kind, view.Root, title,
                ReaderWindowRegistry.DefaultWidth, ReaderWindowRegistry.DefaultHeight, registry.Windows.Sizes);
            view.AttachWindow(window, key, () => Task.Run(() => DeleteFolder(workingFolder)));
            window.Present();
            view.FocusBody();
            opened = true;
        });
        if (!opened)
        {
            await view.ReleaseAsync();
            DeleteFolder(workingFolder);
        }
        return opened;
    }

    private static void DeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch (IOException exception) { Serilog.Log.Warning(exception, "A message preview folder could not be deleted."); }
        catch (UnauthorizedAccessException exception) { Serilog.Log.Warning(exception, "A message preview folder could not be deleted."); }
    }
}
