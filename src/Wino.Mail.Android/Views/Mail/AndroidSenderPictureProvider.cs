using Android.Graphics;
using Android.OS;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Interfaces;
using Wino.Messaging.UI;

namespace Wino.Mail.Android.Views.Mail;

/// <summary>
/// Sender pictures for mail rows and the reader (the UIKitSenderPictureProvider twin): looks each address up through
/// <see cref="IThumbnailService"/> once, decodes the cached file at avatar size and raises <see cref="PictureChanged"/> so
/// visible rows re-map. A new thumbnail (<see cref="ThumbnailAdded"/>) reloads that address; picture preferences reset
/// the cache. Main thread.
/// </summary>
public sealed class AndroidSenderPictureProvider : IRecipient<ThumbnailAdded>
{
    private const int DecodeSize = 160;

    private readonly IThumbnailService _thumbnails;
    private readonly Handler _main = new(Looper.MainLooper ?? throw new InvalidOperationException("The main looper is unavailable."));
    private readonly Dictionary<string, Bitmap?> _images = new(StringComparer.Ordinal);
    private readonly Dictionary<string, object> _loading = new(StringComparer.Ordinal);

    public AndroidSenderPictureProvider(IThumbnailService thumbnails, IPreferencesService preferences)
    {
        _thumbnails = thumbnails;
        preferences.PreferenceChanged += PreferenceChanged;
        WeakReferenceMessenger.Default.Register(this);
    }

    /// <summary>Raised with the address whose picture changed, or null when every picture was reset.</summary>
    public event EventHandler<string?>? PictureChanged;

    /// <summary>The cached picture for <paramref name="address"/>, or null while unknown or when there is none.</summary>
    public Bitmap? Get(string? address)
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
        Bitmap? image = null;
        try
        {
            var path = (await _thumbnails.GetThumbnailAsync(key).ConfigureAwait(false))?.FilePath;
            if (path is not null && File.Exists(path)) image = Decode(path);
        }
        catch (Exception exception) { Serilog.Log.Debug(exception, "The sender picture lookup failed."); }
        OnMain(() =>
        {
            if (!_loading.TryGetValue(key, out var current) || !ReferenceEquals(current, token)) return;
            _loading.Remove(key);
            _images.TryGetValue(key, out var previous);
            _images[key] = image;
            if (image is not null || previous is not null) PictureChanged?.Invoke(this, key);
        });
    }

    /// <summary>Decodes at most <see cref="DecodeSize"/> pixels on a side.</summary>
    private static Bitmap? Decode(string path)
    {
        var bounds = new BitmapFactory.Options { InJustDecodeBounds = true };
        BitmapFactory.DecodeFile(path, bounds);
        int sample = 1;
        while (bounds.OutWidth / (sample * 2) >= DecodeSize && bounds.OutHeight / (sample * 2) >= DecodeSize) sample *= 2;
        return BitmapFactory.DecodeFile(path, new BitmapFactory.Options { InSampleSize = sample });
    }

    private static string? Normalize(string? address)
        => string.IsNullOrWhiteSpace(address) ? null : address.Trim().ToLowerInvariant();

    private void OnMain(Action action)
    {
        if (Looper.MyLooper() == Looper.MainLooper) action();
        else _main.Post(action);
    }
}
