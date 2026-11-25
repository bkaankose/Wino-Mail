using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Contacts;

/// <summary>
/// Contact photos by picture file id (macOS LoadPicture): the file is read off the main thread, decoded once, cached
/// and announced through <see cref="Loaded"/>. A missing file is not cached, so a photo stored later still shows.
/// </summary>
internal sealed class ContactPictures : IDisposable
{
    private const int MinimumSide = 8;

    private readonly IPictureStorageService _storage;
    private readonly IDispatcher _dispatcher;
    private readonly Action<Exception> _error;
    private readonly TextureCache _cache = new(512);
    private readonly HashSet<Guid> _loading = [];
    private bool _disposed;

    public ContactPictures(IPictureStorageService storage, IDispatcher dispatcher, Action<Exception> error)
    {
        _storage = storage;
        _dispatcher = dispatcher;
        _error = error;
    }

    /// <summary>Raised on the main thread with the file id of a photo that just became available.</summary>
    public event Action<Guid>? Loaded;

    public Gdk.Texture? Get(AccountContactViewModel contact) => contact.ContactPictureFileId is { } id ? Get(id) : null;

    public Gdk.Texture? Get(Guid id)
    {
        if (_disposed) return null;
        var key = id.ToString("N");
        if (_cache.TryGet(key, out var cached)) return cached;
        if (_loading.Add(id)) Load(id, key);
        return null;
    }

    public void Dispose()
    {
        _disposed = true;
        Loaded = null;
        _cache.Clear();
        _loading.Clear();
    }

    private async void Load(Guid id, string key)
    {
        try
        {
            var path = _storage.GetPicturePath(PictureKind.Contact, id);
            var bytes = await Textures.ReadAsync(path).ConfigureAwait(false);
            await _dispatcher.ExecuteOnUIThread(() =>
            {
                _loading.Remove(id);
                if (_disposed || bytes is null) return;
                var texture = Textures.FromBytes(bytes);
                // Some CardDAV servers store a 1x1 PHOTO; it would draw an empty circle where the initials belong.
                if (texture is null || texture.GetWidth() < MinimumSide || texture.GetHeight() < MinimumSide) return;
                _cache.Set(key, texture);
                Loaded?.Invoke(id);
            });
        }
        catch (Exception exception)
        {
            _error(exception);
        }
    }
}
