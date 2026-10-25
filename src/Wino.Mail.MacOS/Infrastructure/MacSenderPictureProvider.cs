using AppKit;
using CommunityToolkit.Mvvm.Messaging;
using Foundation;
using Wino.Core.Domain.Interfaces;
using Wino.Messaging.UI;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>
/// Sender pictures (Gravatar, then favicon) for AppKit avatars, the role the Windows
/// WinoContactPicture lookup plays. <see cref="IThumbnailService"/> downloads and caches the files;
/// this keeps the decoded images by address. A finished lookup that found or removed a picture raises
/// <see cref="PictureChanged"/> on the main thread with the address, or null after a preference
/// change that resets every picture. All state is main-thread only.
/// </summary>
public sealed class MacSenderPictureProvider : IRecipient<ThumbnailAdded>
{
    private readonly IThumbnailService _thumbnails;
    private readonly Dictionary<string, NSImage?> _images = new(StringComparer.Ordinal);
    private readonly Dictionary<string, object> _loading = new(StringComparer.Ordinal);

    public MacSenderPictureProvider(IThumbnailService thumbnails, IPreferencesService preferences)
    {
        _thumbnails = thumbnails;
        preferences.PreferenceChanged += PreferenceChanged;
        WeakReferenceMessenger.Default.Register(this);
    }

    /// <summary>Raised with the address whose picture changed, or null when every picture was reset.</summary>
    public event EventHandler<string?>? PictureChanged;

    /// <summary>
    /// The cached picture for <paramref name="address"/>, or null while unknown or when there is none.
    /// An unknown address starts a lookup; <see cref="PictureChanged"/> follows if it finds a picture.
    /// </summary>
    public NSImage? Get(string? address)
    {
        if (Normalize(address) is not { } key) return null;
        if (_images.TryGetValue(key, out var image)) return image;
        if (!_loading.ContainsKey(key)) Load(key);
        return null;
    }

    /// <summary>The thumbnail service stored new files for an address: look it up again.</summary>
    public void Receive(ThumbnailAdded message)
    {
        if (Normalize(message.Email) is not { } key) return;
        // The old picture stays until the new lookup replaces it.
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
        // A newer lookup for the same address (or a reset) replaces the token and drops this result.
        var token = new object();
        _loading[key] = token;
        string? path = null;
        NSImage? image = null;
        try
        {
            path = (await _thumbnails.GetThumbnailAsync(key).ConfigureAwait(false))?.FilePath;
            // The file check and decode stay on this background continuation; the main thread only stores the result.
            if (path is not null && File.Exists(path)) image = new NSImage(path);
            if (image is { IsValid: false }) image = null;
        }
        catch { image = null; }
        OnMain(() =>
        {
            if (!_loading.TryGetValue(key, out var current) || !ReferenceEquals(current, token)) return;
            _loading.Remove(key);
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
        else NSApplication.SharedApplication.BeginInvokeOnMainThread(action);
    }
}
