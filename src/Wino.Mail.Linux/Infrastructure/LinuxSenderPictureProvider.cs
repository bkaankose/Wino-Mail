using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Messaging.UI;
using Wino.Mail.Controls.Core;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Infrastructure;

/// <summary>
/// Sender pictures (Gravatar, then favicon) for avatars. <see cref="IThumbnailService"/> downloads and caches the files;
/// this keeps the textures by address. A finished lookup that found or removed a picture raises
/// <see cref="PictureChanged"/> on the main thread with the address, or null after a preference change that reset every
/// picture. All state is main-thread only.
/// </summary>
internal sealed class LinuxSenderPictureProvider : IRecipient<ThumbnailAdded>, IDisposable
{
    private readonly IThumbnailService _thumbnails;
    private readonly IPreferencesService _preferences;
    private readonly IDispatcher _dispatcher;
    private readonly IMessenger _messenger;
    private readonly IWinoLogger _logger;
    private readonly TextureCache _pictures = new(512);
    private readonly TextureCache _localPictures = new(64);
    private readonly Dictionary<string, object> _loading = new(StringComparer.Ordinal);
    private readonly EventHandler<string> _preferenceChanged;
    private bool _disposed;

    public LinuxSenderPictureProvider(IThumbnailService thumbnails, IPreferencesService preferences, IDispatcher dispatcher, IMessenger messenger, IWinoLogger logger)
    {
        _thumbnails = thumbnails;
        _preferences = preferences;
        _dispatcher = dispatcher;
        _messenger = messenger;
        _logger = logger;
        _preferenceChanged = PreferenceChanged;
        preferences.PreferenceChanged += _preferenceChanged;
        messenger.Register<ThumbnailAdded>(this);
    }

    public bool IsEnabled => _preferences.IsShowSenderPicturesEnabled;

    public event EventHandler<string?>? PictureChanged;

    /// <summary>The cached picture, or null while unknown or when there is none. An unknown address starts a lookup.</summary>
    public Gdk.Texture? Get(string? address)
    {
        if (!IsEnabled || Normalize(address) is not { } key) return null;
        if (_pictures.TryGet(key, out var texture)) return texture;
        if (!_loading.ContainsKey(key)) _ = LoadAsync(key);
        return null;
    }

    /// <summary>A contact or profile photo (<see cref="IContactPicture.LocalImagePath"/>) first, then the address.</summary>
    public Gdk.Texture? Get(IContactPicture? contact)
    {
        if (contact is null) return null;
        if (!string.IsNullOrWhiteSpace(contact.LocalImagePath))
        {
            var path = contact.LocalImagePath;
            if (_localPictures.TryGet(path, out var cached) && cached is not null) return cached;
            if (Textures.FromFile(path) is { } texture)
            {
                _localPictures.Set(path, texture);
                return texture;
            }
        }
        return Get(contact.Address);
    }

    public static string? Normalize(string? address)
        => string.IsNullOrWhiteSpace(address) ? null : address.Trim().ToLowerInvariant();

    public void Receive(ThumbnailAdded message)
        => _ = _dispatcher.ExecuteOnUIThread(() =>
        {
            if (!_disposed && Normalize(message.Email) is { } key) _ = LoadAsync(key);
        });

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _preferences.PreferenceChanged -= _preferenceChanged;
        _messenger.Unregister<ThumbnailAdded>(this);
        _pictures.Clear();
        _localPictures.Clear();
        _loading.Clear();
    }

    private void PreferenceChanged(object? sender, string name)
    {
        if (name is not (nameof(IPreferencesService.IsShowSenderPicturesEnabled)
            or nameof(IPreferencesService.IsGravatarEnabled)
            or nameof(IPreferencesService.IsFaviconEnabled))) return;
        _ = _dispatcher.ExecuteOnUIThread(() =>
        {
            if (_disposed) return;
            _pictures.Clear();
            _localPictures.Clear();
            _loading.Clear();
            PictureChanged?.Invoke(this, null);
        });
    }

    private async Task LoadAsync(string key)
    {
        try
        {
            // A newer lookup for the same address, or a reset, replaces the token and drops this result.
            var token = new object();
            _loading[key] = token;
            var result = await _thumbnails.GetThumbnailAsync(key).ConfigureAwait(false);
            var bytes = await Textures.ReadAsync(result?.FilePath).ConfigureAwait(false);
            await _dispatcher.ExecuteOnUIThread(() => Complete(key, token, bytes));
        }
        catch (Exception exception)
        {
            _logger.CaptureException(exception);
        }
    }

    private void Complete(string key, object token, byte[]? bytes)
    {
        if (_disposed || !_loading.TryGetValue(key, out var current) || !ReferenceEquals(current, token)) return;
        _loading.Remove(key);
        var texture = Textures.FromBytes(bytes);
        _pictures.TryGet(key, out var previous);
        _pictures.Set(key, texture);
        if (texture is not null || previous is not null) PictureChanged?.Invoke(this, key);
    }
}
