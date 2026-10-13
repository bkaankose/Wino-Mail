using CommunityToolkit.Mvvm.Messaging;
using Foundation;
using QuickLook;
using UIKit;
using WebKit;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Platform;
using Wino.Core.Domain.Models.Printing;
using Wino.Messaging.UI;
using Wino.Platform.iOS.Services;

namespace Wino.Mail.UIKit.Infrastructure;

/// <summary>Reports whether the scene's effective appearance is dark; the reader uses it for its initial theme.</summary>
public sealed class UIKitUnderlyingThemeService(IUIKitShellHost host) : IUnderlyingThemeService
{
    public bool IsUnderlyingThemeDark()
    {
        if (NSThread.IsMain) return IsDark();
        bool dark = false;
        UIApplication.SharedApplication.InvokeOnMainThread(() => dark = IsDark());
        return dark;
    }

    private bool IsDark()
    {
        var traits = host.Window?.TraitCollection ?? UITraitCollection.CurrentTraitCollection;
        return traits.UserInterfaceStyle == UIUserInterfaceStyle.Dark;
    }
}

/// <summary>
/// The view controller to present system sessions from (MSAL web view, ASWebAuthenticationSession anchor, QuickLook,
/// pickers): the top-most presented controller of the scene window. Wino.Platform.iOS takes <see cref="Presenter"/>.
/// </summary>
public sealed class UIKitAuthenticationPresenter(IUIKitShellHost host)
{
    /// <summary>The top-most controller whose view is in a window, or null while nothing is on screen. Main thread.</summary>
    public UIViewController? Presenter() => host.TopViewController is { ViewIfLoaded.Window: not null } top ? top : null;
}

/// <summary>
/// Shows local files with QuickLook (<see cref="IIOSFilePresenter"/>): attachments, exported PDFs and saved files.
/// QuickLook offers the share sheet for "open in" and saving to Files.
/// </summary>
public sealed class UIKitFilePresenter(IDispatcher dispatcher, IUIKitShellHost host) : IIOSFilePresenter
{
    public async Task<PlatformOperationResult> PresentFileAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(path)) return new PlatformOperationResult(PlatformOperationStatus.Failed, "The file does not exist.");
        var result = new PlatformOperationResult(PlatformOperationStatus.Unavailable);
        await dispatcher.ExecuteOnUIThread(() =>
        {
            if (host.TopViewController is not { } presenter) return;
            var preview = new QLPreviewController { DataSource = new SingleFileSource(NSUrl.FromFilename(path)) };
            presenter.PresentViewController(preview, true, null);
            result = new PlatformOperationResult(PlatformOperationStatus.Succeeded);
        });
        return result;
    }

    private sealed class SingleFileSource(NSUrl url) : QLPreviewControllerDataSource
    {
        public override nint PreviewItemCount(QLPreviewController controller) => 1;
        public override IQLPreviewItem GetPreviewItem(QLPreviewController controller, nint index) => new FileItem(url);
    }

    private sealed class FileItem(NSUrl url) : QLPreviewItem
    {
        public override NSUrl PreviewItemUrl => url;
        public override string PreviewItemTitle => url.LastPathComponent ?? string.Empty;
    }
}

/// <summary>
/// Reader-owned print and PDF presentation (Windows WindowsMailPrintPresenter, Mac MacMailPrintPresenter). Printing
/// presents UIPrintInteractionController with the web view's print formatter; PDF export writes
/// <c>WKWebView.CreatePdf</c> to the chosen path. Both use the message exactly as the reader renders it. The reader
/// creates one per page with a function that returns its web view while the page is alive.
/// </summary>
public sealed class UIKitMailPrintPresenter(Func<WKWebView?> webView, IDispatcher dispatcher) : IMailPrintPresenter
{
    public async Task<PrintingResult> PrintAsync(MailPrintRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task<PrintingResult> operation = Task.FromResult(PrintingResult.Unavailable);
        await dispatcher.ExecuteOnUIThread(() => operation = BeginPrint(request));
        return await operation;
    }

    private Task<PrintingResult> BeginPrint(MailPrintRequest request)
    {
        if (webView() is not { Window: not null } view) return Task.FromResult(PrintingResult.Unavailable);
        if (!UIPrintInteractionController.PrintingAvailable) return Task.FromResult(PrintingResult.Unavailable);
        var controller = UIPrintInteractionController.SharedPrintController;
        // One print panel at a time; a second request while it is open is abandoned, like the Mac sheet check.
        if (IsPresenting) return Task.FromResult(PrintingResult.Abandoned);
        var info = UIPrintInfo.PrintInfo;
        info.OutputType = UIPrintInfoOutputType.General;
        info.JobName = string.IsNullOrWhiteSpace(request.Title) ? "Wino Mail" : request.Title;
        controller.PrintInfo = info;
        controller.PrintFormatter = view.ViewPrintFormatter;
        var completion = new TaskCompletionSource<PrintingResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        IsPresenting = true;
        void Completed(UIPrintInteractionController? printController, bool completed, NSError? error)
        {
            IsPresenting = false;
            completion.TrySetResult(error is not null ? PrintingResult.Failed : completed ? PrintingResult.Submitted : PrintingResult.Canceled);
        }
        bool presented = UIDevice.CurrentDevice.UserInterfaceIdiom == UIUserInterfaceIdiom.Pad
            ? controller.PresentFromRectInView(view.Bounds, view, true, Completed)
            : controller.Present(true, Completed);
        if (!presented)
        {
            IsPresenting = false;
            completion.TrySetResult(PrintingResult.Failed);
        }
        return completion.Task;
    }

    private static bool IsPresenting { get; set; }

    public async Task<PlatformOperationResult> ExportPdfAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(path))
            return new PlatformOperationResult(PlatformOperationStatus.Failed, "No PDF path was given.");

        Task<NSData>? operation = null;
        await dispatcher.ExecuteOnUIThread(() =>
        {
            if (webView() is { } view) operation = view.CreatePdfAsync(new WKPdfConfiguration());
        });
        if (operation is null) return new PlatformOperationResult(PlatformOperationStatus.Unavailable);
        try
        {
            using var data = await operation;
            if (cancellationToken.IsCancellationRequested) return new PlatformOperationResult(PlatformOperationStatus.Cancelled);
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            await File.WriteAllBytesAsync(path, data.ToArray(), cancellationToken);
            return new PlatformOperationResult(PlatformOperationStatus.Succeeded);
        }
        catch (OperationCanceledException)
        {
            return new PlatformOperationResult(PlatformOperationStatus.Cancelled);
        }
        catch (Exception exception)
        {
            return new PlatformOperationResult(PlatformOperationStatus.Failed, exception.Message);
        }
    }
}

/// <summary>
/// Sender pictures (Gravatar, then favicon) for UIKit avatars, the Mac MacSenderPictureProvider. <see cref="IThumbnailService"/>
/// downloads and caches the files; this keeps the decoded images by address. A finished lookup that found or removed a
/// picture raises <see cref="PictureChanged"/> on the main thread with the address, or null after a preference change
/// that resets every picture. All state is main-thread only.
/// </summary>
public sealed class UIKitSenderPictureProvider : IRecipient<ThumbnailAdded>
{
    private readonly IThumbnailService _thumbnails;
    private readonly Dictionary<string, UIImage?> _images = new(StringComparer.Ordinal);
    private readonly Dictionary<string, object> _loading = new(StringComparer.Ordinal);

    public UIKitSenderPictureProvider(IThumbnailService thumbnails, IPreferencesService preferences)
    {
        _thumbnails = thumbnails;
        preferences.PreferenceChanged += PreferenceChanged;
        WeakReferenceMessenger.Default.Register(this);
    }

    /// <summary>Raised with the address whose picture changed, or null when every picture was reset.</summary>
    public event EventHandler<string?>? PictureChanged;

    /// <summary>The cached picture for <paramref name="address"/>, or null while unknown or when there is none.</summary>
    public UIImage? Get(string? address)
    {
        if (Normalize(address) is not { } key) return null;
        if (_images.TryGetValue(key, out var image)) return image;
        if (!_loading.ContainsKey(key)) Load(key);
        return null;
    }

    public void Receive(ThumbnailAdded message)
    {
        if (Normalize(message.Email) is not { } key) return;
        OnMain(() => Load(key));
    }

    private void PreferenceChanged(object? sender, string name)
    {
        if (name is not (nameof(IPreferencesService.IsShowSenderPicturesEnabled)
            or nameof(IPreferencesService.IsGravatarEnabled)
            or nameof(IPreferencesService.IsFaviconEnabled))) return;
        OnMain(() =>
        {
            _images.Clear();
            _loading.Clear();
            PictureChanged?.Invoke(this, null);
        });
    }

    private async void Load(string key)
    {
        var token = new object();
        _loading[key] = token;
        string? path = null;
        try { path = (await _thumbnails.GetThumbnailAsync(key).ConfigureAwait(false))?.FilePath; }
        catch (Exception exception) { Serilog.Log.Debug(exception, "The sender picture lookup failed."); }
        OnMain(() =>
        {
            if (!_loading.TryGetValue(key, out var current) || !ReferenceEquals(current, token)) return;
            _loading.Remove(key);
            var image = path is not null && File.Exists(path) ? UIImage.FromFile(path) : null;
            _images.TryGetValue(key, out var previous);
            _images[key] = image;
            if (image is not null || previous is not null) PictureChanged?.Invoke(this, key);
        });
    }

    private static string? Normalize(string? address)
        => string.IsNullOrWhiteSpace(address) ? null : address.Trim().ToLowerInvariant();

    private static void OnMain(Action action)
    {
        if (NSThread.IsMain) action();
        else UIApplication.SharedApplication.BeginInvokeOnMainThread(action);
    }
}
