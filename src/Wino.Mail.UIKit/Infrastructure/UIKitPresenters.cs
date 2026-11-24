using CommunityToolkit.Mvvm.Messaging;
using Foundation;
using QuickLook;
using UIKit;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Platform;
using Wino.Core.Domain.Models.Printing;
using Wino.Platform.iOS.Services;
using Wino.Core.Domain.Messaging.UI;

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
